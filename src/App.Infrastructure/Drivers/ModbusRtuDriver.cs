using System.Collections.Concurrent;
using System.IO.Ports;
using App.Core;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure.Drivers;

/// <summary>
/// Modbus RTU 驱动实现。
/// 基于串口（RS-485 / RS-232）实现功能码 01-10，帧格式 <c>[SlaveAddr][PDU][CRC16]</c>。
/// <para>
/// 帧定界采用"按功能码推导响应长度 + 精确读取"方案（比 3.5 字符静默更可靠、可测），
/// 发送侧通过 <see cref="ModbusRtuDriverConfig.InterFrameDelayMs"/> 满足主站最小帧间隔。
/// 线程安全（单连接串行化请求），完整异步支持。
/// </para>
/// <para>
/// 注意：<see cref="System.IO.Ports.SerialPort"/> 在 macOS 上 <c>Open()</c> 会抛
/// <see cref="PlatformNotSupportedException"/>；本类编译不受影响，真实串口验证在
/// Windows / Linux 环境（P2.10 模拟设备 / P7.5 现场联调）。
/// </para>
/// </summary>
public sealed class ModbusRtuDriver : IDeviceDriver
{
    // ── 字段 ───────────────────────────────────────────────
    private readonly ModbusRtuDriverConfig _config;
    private readonly ILogger<ModbusRtuDriver> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private readonly int _interFrameDelayMs;

    private SerialPort? _serialPort;

    // 订阅管理
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _subscriptions = new();
    private readonly CancellationTokenSource _disposalCts = new();
    private Task? _pollTask;

    // 连接状态
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// 初始化 Modbus RTU 驱动。
    /// </summary>
    public ModbusRtuDriver(ModbusRtuDriverConfig config, ILogger<ModbusRtuDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
        _interFrameDelayMs = config.GetEffectiveInterFrameDelayMs();
    }

    // ── IDeviceDriver 属性 ─────────────────────────────────

    /// <inheritdoc />
    public string DriverName => $"ModbusRTU-{_config.DeviceId}";

    /// <inheritdoc />
    public DriverConfigBase Config => _config;

    /// <inheritdoc />
    public ConnectionState State
    {
        get => _state;
        private set
        {
            var oldState = Interlocked.Exchange(ref _state, value);
            if (oldState != value)
            {
                OnConnectionStateChanged(oldState, value);
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    // ── 连接管理 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask<Result> ConnectAsync(CancellationToken ct = default)
    {
        if (State is ConnectionState.Connected or ConnectionState.Connecting)
        {
            return Result.Success();
        }

        State = ConnectionState.Connecting;
        _logger.LogInformation(
            "{Driver} 正在打开串口 {Port} (Baud={Baud}, Data={Data}, Parity={Parity}, Stop={Stop}) ...",
            DriverName, _config.PortName, _config.BaudRate, _config.DataBits, _config.Parity, _config.StopBits);

        try
        {
            var port = new SerialPort(
                _config.PortName,
                _config.BaudRate,
                ToSystemParity(_config.Parity),
                _config.DataBits,
                ToSystemStopBits(_config.StopBits))
            {
                // 读超时用于 SerialPort 同步读取的兜底（异步路径另有 CancellationToken）
                ReadTimeout = _config.ReadTimeoutMs > 0 ? _config.ReadTimeoutMs : _config.TimeoutMs,
                WriteTimeout = _config.TimeoutMs,
                Handshake = Handshake.None,
                DtrEnable = true,
                RtsEnable = false
            };

            // SerialPort.Open() 是同步阻塞 API，放到线程池执行避免阻塞调用方
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.TimeoutMs);
            await Task.Run(() => port.Open(), timeoutCts.Token).ConfigureAwait(false);

            _serialPort = port;
            State = ConnectionState.Connected;
            _logger.LogInformation("{Driver} 串口已打开 ({Port})", DriverName, _config.PortName);

            StartPollIfNeeded();
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            State = ConnectionState.Disconnected;
            _logger.LogWarning("{Driver} 打开串口超时 ({Timeout}ms)", DriverName, _config.TimeoutMs);
            return Result.Failure($"打开串口超时 ({_config.TimeoutMs}ms)");
        }
        catch (Exception ex)
        {
            // 尝试清理半开的串口
            SafeCloseSerialPort();
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} 打开串口失败 ({Port})", DriverName, _config.PortName);
            return Result.Failure($"打开串口失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public async ValueTask<Result> DisconnectAsync(CancellationToken ct = default)
    {
        if (State is ConnectionState.Disconnected)
        {
            return Result.Success();
        }

        State = ConnectionState.Disconnecting;
        _logger.LogInformation("{Driver} 正在关闭串口...", DriverName);

        try
        {
            await CleanupConnectionAsync().ConfigureAwait(false);
            State = ConnectionState.Disconnected;
            _logger.LogInformation("{Driver} 已断开", DriverName);
            return Result.Success();
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} 断开时发生异常", DriverName);
            return Result.Failure($"断开异常: {ex.Message}");
        }
    }

    // ── 读写操作 ─────────────────────────────────────────────

    /// <inheritdoc />
    /// <remarks>
    /// 地址格式与 ModbusTcpDriver 一致：
    /// <list type="bullet">
    ///   <item><c>03:100</c> / <c>100</c> → 读保持寄存器</item>
    ///   <item><c>01:0</c> → 读线圈；<c>02:0</c> → 读离散输入；<c>04:100</c> → 读输入寄存器</item>
    ///   <item>支持 PLC 地址格式（4xxxxx/3xxxxx/1xxxxx）</item>
    /// </list>
    /// length：FC01/02 为位数量，FC03/04 为寄存器数量。
    /// </remarks>
    public async ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        if (!ModbusProtocol.TryParseReadAddress(address, out var functionCode, out var startAddress))
        {
            return Result<byte[]>.Failure($"无效地址格式: {address}");
        }

        if (functionCode is not (ModbusFunctionCode.ReadCoils
                or ModbusFunctionCode.ReadDiscreteInputs
                or ModbusFunctionCode.ReadHoldingRegisters
                or ModbusFunctionCode.ReadInputRegisters))
        {
            return Result<byte[]>.Failure($"功能码 0x{functionCode:X2} 不是读操作，地址: {address}");
        }

        var pdu = ModbusProtocol.BuildReadRequestPdu(functionCode, startAddress, length);

        return await ExecuteRequestAsync(
            functionCode,
            length,
            pdu,
            responsePdu => ModbusProtocol.ParseReadResponse(responsePdu),
            ct
        ).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 地址格式与 ModbusTcpDriver 一致：默认按 data.Length 推断功能码（1=单线圈，2=单寄存器），
    /// 多写需用前缀 <c>0F:</c> / <c>10:</c> 显式指定。
    /// </remarks>
    public async ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!ModbusProtocol.TryParseWriteAddress(address, data.Length, out var functionCode, out var startAddress))
        {
            return Result.Failure($"无效地址或数据: address={address}, data.Length={data.Length}");
        }

        var pdu = ModbusProtocol.BuildWriteRequestPdu(functionCode, startAddress, data);
        if (pdu is null)
        {
            return Result.Failure($"无法为功能码 0x{functionCode:X2} 构建写请求");
        }

        return await ExecuteRequestAsync(
            functionCode,
            (ushort)(data.Length / 2),
            pdu,
            responsePdu =>
            {
                if (!ModbusProtocol.IsWriteResponseWellFormed(responsePdu))
                {
                    return Result.Failure("写操作响应帧过短");
                }

                var respAddr = ModbusProtocol.ReadBigEndian(responsePdu.AsSpan(1..3));
                if (respAddr != startAddress)
                {
                    _logger.LogWarning(
                        "{Driver} 写操作返回地址不匹配: 期望={Expected}, 实际={Actual}",
                        DriverName, startAddress, respAddr);
                }

                return Result.Success();
            },
            ct
        ).ConfigureAwait(false);
    }

    // ── 订阅 ─────────────────────────────────────────────────

    /// <inheritdoc />
    /// <summary>
    /// 订阅地址的值变化。内部通过后台轮询实现，轮询间隔由
    /// <see cref="DriverConfigBase.HeartbeatIntervalMs"/> 控制（默认 30s）。
    /// </summary>
    public ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(callback);

        var entry = new SubscriptionEntry(address, callback);
        if (_subscriptions.TryAdd(address, entry))
        {
            _logger.LogDebug("{Driver} 新增订阅: {Address}", DriverName, address);
            StartPollIfNeeded();
        }

        return ValueTask.FromResult<IDisposable>(new SubscriptionHandle(this, address));
    }

    // ── 异步释放 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("{Driver} 正在释放资源...", DriverName);

        await _disposalCts.CancelAsync().ConfigureAwait(false);
        _disposalCts.Dispose();

        _subscriptions.Clear();
        await CleanupConnectionAsync().ConfigureAwait(false);
        _requestLock.Dispose();

        _logger.LogInformation("{Driver} 资源已释放", DriverName);
    }

    // ── 内部实现：请求执行 ───────────────────────────────────

    /// <summary>
    /// 执行 Modbus RTU 请求：加 CRC → 发送 → 精确读取响应 → 校验 CRC → 解析。
    /// </summary>
    /// <param name="context">上下文（仅用于日志/调试）。</param>
    /// <param name="expectedLength">
    /// 非异常响应的期望帧长度；若实际响应为异常帧则用 <see cref="ModbusProtocol.RtuExceptionFrameLength"/>。
    /// </param>
    /// <param name="parseResponse">将校验通过的 PDU 转换为最终结果。</param>
    private async ValueTask<TResult> ExecuteRequestAsync<TResult>(
        ModbusFunctionCode functionCode,
        ushort quantity,
        byte[] requestPdu,
        Func<byte[], TResult> parseResponse,
        CancellationToken ct)
    {
        if (State != ConnectionState.Connected)
        {
            var msg = $"{DriverName} 未连接，无法执行操作";
            _logger.LogWarning(msg);
            return CreateFailureResult<TResult>(msg);
        }

        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (State != ConnectionState.Connected)
            {
                return CreateFailureResult<TResult>($"{DriverName} 连接已断开");
            }

            var port = _serialPort;
            var baseStream = port?.BaseStream;
            if (port is null || baseStream is null)
            {
                return CreateFailureResult<TResult>("串口对象为空");
            }

            // ── 发送请求帧：[Slave][PDU][CRC16] ──────────────
            var frame = BuildRtuFrame(requestPdu);

            // 满足 RTU 3.5 字符帧间隔（主站连续发送时的最小静默）
            if (_interFrameDelayMs > 0)
            {
                await Task.Delay(_interFrameDelayMs, ct).ConfigureAwait(false);
            }

            // 发送前清空接收缓冲区的残留数据（避免上一帧碎片干扰）
            port.DiscardInBuffer();

            await baseStream.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
            await baseStream.FlushAsync(ct).ConfigureAwait(false);

            _logger.LogTrace(
                "{Driver} Tx FC=0x{FC:X2} FrameLen={Len}",
                DriverName, (byte)functionCode, frame.Length);

            // ── 接收响应：先读 2 字节判断是否异常，再决定总长度 ──
            // 第 1 字节 = Slave 地址，第 2 字节 = 功能码（最高位为异常标志）
            var header = new byte[2];
            if (!await ReadExactAsync(baseStream, header, ct).ConfigureAwait(false))
            {
                _ = HandleConnectionLostAsync("读取响应头失败：连接可能已断开");
                return CreateFailureResult<TResult>("读取响应头失败：连接可能已断开");
            }

            // 校验响应 Slave 地址
            if (header[0] != _config.SlaveAddress)
            {
                _logger.LogWarning(
                    "{Driver} Rx Slave 地址不匹配: 期望={Expected}, 实际={Actual}",
                    DriverName, _config.SlaveAddress, header[0]);
                // 不直接失败：可能是线路噪声，丢弃当前帧后返回错误
                return CreateFailureResult<TResult>($"响应 Slave 地址不匹配: 期望 {_config.SlaveAddress}, 实际 {header[0]}");
            }

            var isException = ModbusProtocol.IsExceptionResponse(header[1]);
            var remaining = isException
                ? ModbusProtocol.RtuExceptionFrameLength - 2   // 剩余：异常码(1) + CRC(2) = 3
                : ModbusProtocol.GetRtuResponseFrameLength(functionCode, quantity) - 2;

            if (remaining <= 0)
            {
                return CreateFailureResult<TResult>($"无法推导响应长度 (FC=0x{functionCode:X2})");
            }

            // ── 读取响应剩余部分（含 CRC） ──────────────────
            var rest = new byte[remaining];
            if (!await ReadExactAsync(baseStream, rest, ct).ConfigureAwait(false))
            {
                _ = HandleConnectionLostAsync("读取响应体失败：连接可能已断开");
                return CreateFailureResult<TResult>("读取响应体失败：连接可能已断开");
            }

            // 拼装完整帧用于 CRC 校验：[Slave][FC][...][CRC]
            var fullFrame = new byte[2 + remaining];
            fullFrame[0] = header[0];
            fullFrame[1] = header[1];
            Buffer.BlockCopy(rest, 0, fullFrame, 2, remaining);

            // ── CRC 校验 ──────────────────────────────────
            if (!Crc16.Verify(fullFrame))
            {
                _logger.LogWarning("{Driver} Rx CRC 校验失败，丢弃帧 (FC=0x{FC:X2}, FrameLen={Len})",
                    DriverName, (byte)functionCode, fullFrame.Length);
                return CreateFailureResult<TResult>("响应帧 CRC 校验失败");
            }

            // ── 异常响应处理 ────────────────────────────────
            if (isException)
            {
                var exceptionCode = fullFrame.Length > 2 ? fullFrame[2] : (byte)0;
                var errorMsg = ModbusProtocol.FormatModbusError(exceptionCode);
                _logger.LogWarning("{Driver} Rx 异常: FC=0x{FC:X2} Code={Code}: {Msg}",
                    DriverName, (byte)functionCode, exceptionCode, errorMsg);
                return CreateFailureResult<TResult>(errorMsg);
            }

            // ── 提取 PDU（去掉 Slave 和 CRC）并解析 ──────────
            var pduLength = fullFrame.Length - 1 - 2; // 去 Slave(1) 和 CRC(2)
            var responsePdu = new byte[pduLength];
            Buffer.BlockCopy(fullFrame, 1, responsePdu, 0, pduLength);

            var responseFC = ModbusProtocol.GetResponseFunctionCode(fullFrame[1]);
            if (responseFC != functionCode)
            {
                _logger.LogWarning("{Driver} Rx 功能码不匹配: 期望=0x{Exp:X2} 实际=0x{Act:X2}",
                    DriverName, (byte)functionCode, (byte)responseFC);
            }

            _logger.LogTrace("{Driver} Rx FC=0x{FC:X2} PduLen={Len}",
                DriverName, (byte)responseFC, pduLength);

            return parseResponse(responsePdu);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CreateFailureResult<TResult>("操作已取消");
        }
        catch (IOException ex)
        {
            _logger.LogError(ex, "{Driver} 串口 IO 错误", DriverName);
            _ = HandleConnectionLostAsync(ex.Message);
            return CreateFailureResult<TResult>($"串口 IO 错误: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "{Driver} 串口访问被拒绝", DriverName);
            _ = HandleConnectionLostAsync(ex.Message);
            return CreateFailureResult<TResult>($"串口访问被拒绝: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Driver} 请求异常 FC=0x{FC:X2}", DriverName, (byte)functionCode);
            return CreateFailureResult<TResult>($"请求异常: {ex.Message}");
        }
        finally
        {
            _requestLock.Release();
        }
    }

    /// <summary>
    /// 构建 RTU 请求帧：[Slave][PDU][CRC16]。
    /// </summary>
    private byte[] BuildRtuFrame(byte[] pdu)
    {
        var frame = new byte[1 + pdu.Length + 2];
        frame[0] = _config.SlaveAddress;
        pdu.AsSpan().CopyTo(frame.AsSpan(1));
        Crc16.Write(frame.AsSpan(0, 1 + pdu.Length), frame.AsSpan(1 + pdu.Length));
        return frame;
    }

    /// <summary>
    /// 从串口基础流精确读取指定字节数。
    /// </summary>
    private static async ValueTask<bool> ReadExactAsync(
        Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            Memory<byte> segment = buffer.AsMemory(offset);
            int read;
            try
            {
                read = await stream.ReadAsync(segment, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                // 串口已关闭
                _ = ex;
                return false;
            }

            if (read == 0)
            {
                return false;
            }
            offset += read;
        }
        return true;
    }

    // ── 订阅轮询 ─────────────────────────────────────────────

    private void StartPollIfNeeded()
    {
        if (_pollTask is not null || _subscriptions.IsEmpty)
        {
            return;
        }

        _pollTask = PollSubscriptionsAsync(_disposalCts.Token);
    }

    private async Task PollSubscriptionsAsync(CancellationToken ct)
    {
        var pollInterval = TimeSpan.FromMilliseconds(
            Math.Max(100, _config.HeartbeatIntervalMs));

        _logger.LogDebug("{Driver} 订阅轮询已启动 (间隔={Interval}ms)", DriverName, pollInterval.TotalMilliseconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(pollInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_subscriptions.IsEmpty)
            {
                break;
            }

            if (State != ConnectionState.Connected)
            {
                continue;
            }

            foreach (var (addr, entry) in _subscriptions)
            {
                try
                {
                    var result = await ReadAsync(addr, 1, ct).ConfigureAwait(false);
                    if (result.IsSuccess && result.Value is not null)
                    {
                        entry.InvokeIfChanged(result.Value);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogTrace(ex, "{Driver} 订阅轮询失败: {Address}", DriverName, addr);
                }
            }
        }

        _logger.LogDebug("{Driver} 订阅轮询已停止", DriverName);
        _pollTask = null;
    }

    internal void Unsubscribe(string address)
    {
        if (_subscriptions.TryRemove(address, out _))
        {
            _logger.LogDebug("{Driver} 取消订阅: {Address}", DriverName, address);
        }
    }

    // ── 连接状态管理 ─────────────────────────────────────────

    private async Task HandleConnectionLostAsync(string reason)
    {
        _logger.LogWarning("{Driver} 连接丢失: {Reason}", DriverName, reason);

        if (State != ConnectionState.Connected)
        {
            return;
        }

        State = ConnectionState.Reconnecting;

        try
        {
            await CleanupConnectionAsync().ConfigureAwait(false);

            if (_config.AutoReconnect)
            {
                await AttemptReconnectAsync(_disposalCts.Token).ConfigureAwait(false);
            }
            else
            {
                State = ConnectionState.Disconnected;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Driver} 断线处理异常", DriverName);
            State = ConnectionState.Disconnected;
        }
    }

    private async Task AttemptReconnectAsync(CancellationToken ct)
    {
        for (var attempt = 1; attempt <= _config.ReconnectMaxRetries; attempt++)
        {
            if (ct.IsCancellationRequested)
            {
                State = ConnectionState.Disconnected;
                return;
            }

            var delay = Math.Min(
                _config.ReconnectBaseDelayMs * (int)Math.Pow(2, attempt - 1),
                30_000);

            _logger.LogInformation(
                "{Driver} 重连第 {Attempt}/{Max} 次 (延迟={Delay}ms)...",
                DriverName, attempt, _config.ReconnectMaxRetries, delay);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                State = ConnectionState.Disconnected;
                return;
            }

            var result = await ConnectAsync(ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogInformation("{Driver} 重连成功", DriverName);
                return;
            }

            _logger.LogWarning("{Driver} 第 {Attempt} 次重连失败: {Error}",
                DriverName, attempt, result.Error);
        }

        State = ConnectionState.Disconnected;
        _logger.LogError("{Driver} 重连失败（已尝试 {Max} 次）", DriverName, _config.ReconnectMaxRetries);
    }

    private Task CleanupConnectionAsync()
    {
        SafeCloseSerialPort();
        return Task.CompletedTask;
    }

    private void SafeCloseSerialPort()
    {
        try
        {
            if (_serialPort is { IsOpen: true })
            {
                _serialPort.DiscardInBuffer();
                _serialPort.DiscardOutBuffer();
                _serialPort.Close();
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Driver} 关闭串口时异常", DriverName);
        }
        finally
        {
            try
            {
                _serialPort?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogTrace(ex, "{Driver} 释放串口时异常", DriverName);
            }
            _serialPort = null;
        }
    }

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        var detail = newState switch
        {
            ConnectionState.Connected => $"已连接 ({_config.PortName} @ {_config.BaudRate})",
            ConnectionState.Disconnected => "已断开",
            ConnectionState.Reconnecting => $"正在重连（最多 {_config.ReconnectMaxRetries} 次）",
            _ => string.Empty
        };

        var args = new ConnectionStateChangedEventArgs(oldState, newState, detail);
        ConnectionStateChanged?.Invoke(this, args);
    }

    // ── 串口枚举映射 ─────────────────────────────────────────

    private static Parity ToSystemParity(SerialParity parity) => parity switch
    {
        SerialParity.None => Parity.None,
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => Parity.None
    };

    private static StopBits ToSystemStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.One => StopBits.One,
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One
    };

    // ── 工具方法 ─────────────────────────────────────────────

    /// <summary>
    /// 创建失败结果（兼容 Result 和 Result&lt;T&gt;）。
    /// </summary>
    private static TResult CreateFailureResult<TResult>(string error)
    {
        var type = typeof(TResult);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>))
        {
            var failureMethod = type.GetMethod(
                "Failure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                [typeof(string)]);

            if (failureMethod is not null)
            {
                return (TResult)failureMethod.Invoke(null, [error])!;
            }
        }

        var result = Result.Failure(error);
        return (TResult)(object)result;
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 订阅条目：记录地址和回调，并持有上次值以实现变化检测。
    /// </summary>
    private sealed class SubscriptionEntry
    {
        private readonly Action<byte[]> _callback;
        private byte[]? _lastValue;

        public SubscriptionEntry(string address, Action<byte[]> callback)
        {
            Address = address;
            _callback = callback;
        }

        public string Address { get; }

        /// <summary>
        /// 如果值发生变化则调用回调。
        /// </summary>
        public void InvokeIfChanged(byte[] newValue)
        {
            if (_lastValue is not null &&
                _lastValue.AsSpan().SequenceEqual(newValue))
            {
                return;
            }

            _lastValue = newValue;
            _callback(newValue);
        }
    }

    /// <summary>
    /// 订阅句柄：在释放时从驱动取消订阅。
    /// </summary>
    private sealed class SubscriptionHandle : IDisposable
    {
        private readonly ModbusRtuDriver _driver;
        private readonly string _address;

        public SubscriptionHandle(ModbusRtuDriver driver, string address)
        {
            _driver = driver;
            _address = address;
        }

        public void Dispose()
        {
            _driver.Unsubscribe(_address);
        }
    }
}
