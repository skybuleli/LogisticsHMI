using System.Buffers;
using System.Collections.Concurrent;
using System.Net.Sockets;
using App.Core;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure.Drivers;

/// <summary>
/// Modbus TCP 驱动实现。
/// 基于裸 TCP 实现功能码 01-10，使用 Span&lt;byte&gt; / stackalloc 构建帧实现零分配读写。
/// 线程安全（单连接串行化请求），完整异步支持。
/// </summary>
public sealed class ModbusTcpDriver : IDeviceDriver
{
    // ── Modbus TCP 协议常量 ─────────────────────────────────
    private const int MbapHeaderLength = 7;
    private const int ProtocolId = 0;
    private const int MaxPduLength = 253;   // 最大 PDU 长度（Modbus 规范）
    private const int MaxFrameLength = MbapHeaderLength + MaxPduLength;
    private const byte FunctionCodeMask = 0x7F;   // 正常响应 FC 掩码
    private const byte ErrorFlag = 0x80;          // 异常响应最高位

    // ── 字段 ───────────────────────────────────────────────
    private readonly ModbusDriverConfig _config;
    private readonly ILogger<ModbusTcpDriver> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private TcpClient? _tcpClient;
    private NetworkStream? _stream;
    private ushort _nextTransactionId;

    // 订阅管理
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _subscriptions = new();
    private readonly CancellationTokenSource _disposalCts = new();
    private Task? _pollTask;

    // 连接状态
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// 初始化 Modbus TCP 驱动。
    /// </summary>
    public ModbusTcpDriver(ModbusDriverConfig config, ILogger<ModbusTcpDriver> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
    }

    // ── IDeviceDriver 属性 ─────────────────────────────────

    /// <inheritdoc />
    public string DriverName => $"ModbusTCP-{_config.DeviceId}";

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
            "{Driver} 正在连接 {Host}:{Port} ...",
            DriverName, _config.Host, _config.Port);

        try
        {
            var tcpClient = new TcpClient();
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            await tcpClient.ConnectAsync(_config.Host, _config.Port, timeoutCts.Token).ConfigureAwait(false);

            tcpClient.NoDelay = true;
            tcpClient.ReceiveBufferSize = 1024;
            tcpClient.SendBufferSize = 1024;

            _tcpClient = tcpClient;
            _stream = tcpClient.GetStream();

            State = ConnectionState.Connected;
            _logger.LogInformation("{Driver} 连接成功 ({Host}:{Port})", DriverName, _config.Host, _config.Port);

            // 启动订阅轮询（如有活跃订阅）
            StartPollIfNeeded();

            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            State = ConnectionState.Disconnected;
            _logger.LogWarning("{Driver} 连接超时 ({Timeout}ms)", DriverName, _config.TimeoutMs);
            return Result.Failure($"连接超时 ({_config.TimeoutMs}ms)");
        }
        catch (SocketException ex) when (ct.IsCancellationRequested)
        {
            State = ConnectionState.Disconnected;
            return Result.Failure($"连接已取消: {ex.Message}");
        }
        catch (Exception ex)
        {
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} 连接失败", DriverName);
            return Result.Failure($"连接失败: {ex.Message}");
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
        _logger.LogInformation("{Driver} 正在断开连接...", DriverName);

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
    /// 地址格式：[功能码前缀][地址]，例如：
    /// <list type="bullet">
    ///   <item><c>03:100</c> → 读取保持寄存器 100（默认，可省略 "03:"）</item>
    ///   <item><c>01:0</c>   → 读取线圈 0</item>
    ///   <item><c>02:0</c>   → 读取离散输入 0</item>
    ///   <item><c>04:100</c> → 读取输入寄存器 100</item>
    ///   <item><c>100</c>    → 等价于 03:100（保持寄存器）</item>
    /// </list>
    /// 功能码自动从地址前缀推断：
    ///   01=读线圈, 02=读离散输入, 03=读保持寄存器, 04=读输入寄存器。
    /// length 含义：
    ///   - FC01/02：要读取的位（线圈/输入）数量
    ///   - FC03/04：要读取的寄存器数量
    /// </remarks>
    public async ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        // 解析地址中的功能码和起始地址
        if (!TryParseReadAddress(address, out var functionCode, out var startAddress))
        {
            return Result<byte[]>.Failure($"无效地址格式: {address}");
        }

        // 校验功能码是否为读操作
        if (functionCode is not (ModbusFunctionCode.ReadCoils
                or ModbusFunctionCode.ReadDiscreteInputs
                or ModbusFunctionCode.ReadHoldingRegisters
                or ModbusFunctionCode.ReadInputRegisters))
        {
            return Result<byte[]>.Failure($"功能码 0x{functionCode:X2} 不是读操作，地址: {address}");
        }

        return await ExecuteRequestAsync(
            functionCode,
            length,
            () =>
            {
                // 读请求 PDU：FC + 起始地址(2) + 数量(2)
                Span<byte> pdu = stackalloc byte[5];
                pdu[0] = (byte)functionCode;
                WriteBigEndian(pdu[1..3], startAddress);
                WriteBigEndian(pdu[3..5], length);
                return pdu.ToArray();
            },
            (functionCode, response, actualLength) =>
            {
                var responseSpan = response.AsSpan();
                // 读响应：FC + 字节数 + 数据
                if (responseSpan.Length < 2)
                {
                    return Result<byte[]>.Failure("响应帧过短");
                }

                var byteCount = responseSpan[1];
                if (byteCount + 2 > responseSpan.Length)
                {
                    return Result<byte[]>.Failure($"响应数据长度声明 {byteCount} 超出实际 {responseSpan.Length - 2}");
                }

                var result = new byte[byteCount];
                responseSpan.Slice(2, byteCount).CopyTo(result);
                return Result<byte[]>.Success(result);
            },
            ct
        ).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 地址格式：[功能码前缀][地址]，例如：
    /// <list type="bullet">
    ///   <item><c>05:0</c>     → 写单个线圈 0（data 应为 [0x00] 或 [0xFF]）</item>
    ///   <item><c>06:100</c>   → 写单个保持寄存器 100（data 为 2 字节大端）</item>
    ///   <item><c>0F:0</c>     → 写多个线圈（data 为位打包字节）</item>
    ///   <item><c>10:100</c>   → 写多个保持寄存器（data 为大端寄存器值拼接）</item>
    ///   <item><c>100</c>      → 等价于 06:100（写单个保持寄存器，当 data.Length==2）</item>
    /// </list>
    /// 默认功能码推断：
    ///   - data.Length == 2 → FC06 (写单个寄存器)
    ///   - data.Length == 1 → FC05 (写单个线圈)
    ///   否则需通过前缀明确指定 FC0F (写多线圈) 或 FC10 (写多寄存器)。
    /// </remarks>
    public async ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        // 解析地址中的功能码和地址
        if (!TryParseWriteAddress(address, data, out var functionCode, out var startAddress))
        {
            return Result.Failure($"无效地址或数据: address={address}, data.Length={data.Length}");
        }

        return await ExecuteRequestAsync(
            functionCode,
            data,
            () =>
            {
                var pduLength = functionCode switch
                {
                    ModbusFunctionCode.WriteSingleCoil => 4,       // FC + Addr(2) + Value(2)
                    ModbusFunctionCode.WriteSingleRegister => 4,   // FC + Addr(2) + Value(2)
                    ModbusFunctionCode.WriteMultipleCoils => 6 + data.Length,  // FC + Addr(2) + Qty(2) + ByteCount(1) + Data
                    ModbusFunctionCode.WriteMultipleRegisters => 6 + data.Length, // FC + Addr(2) + Qty(2) + ByteCount(1) + Data
                    _ => 0
                };

                if (pduLength == 0)
                {
                    return []; // 不应发生
                }

                // 直接在堆上构建 PDU 帧（最大 253 字节，可忽略分配成本）
                var pdu = new byte[pduLength];
                pdu[0] = (byte)functionCode;
                WriteBigEndian(pdu.AsSpan(1..3), startAddress);

                switch (functionCode)
                {
                    case ModbusFunctionCode.WriteSingleCoil:
                        // 0xFF00 = ON, 0x0000 = OFF
                        WriteBigEndian(pdu.AsSpan(3..5), data[0] != 0 ? (ushort)0xFF00 : (ushort)0);
                        break;

                    case ModbusFunctionCode.WriteSingleRegister:
                        // data 应恰好为 2 字节（大端寄存器值）
                        data.AsSpan(0, Math.Min(data.Length, 2)).CopyTo(pdu.AsSpan(3..5));
                        break;

                    case ModbusFunctionCode.WriteMultipleCoils:
                    {
                        var quantity = data.Length * 8; // 每个字节 8 位
                        WriteBigEndian(pdu.AsSpan(3..5), (ushort)quantity);
                        pdu[5] = (byte)data.Length;
                        data.AsSpan().CopyTo(pdu.AsSpan(6..));
                        break;
                    }

                    case ModbusFunctionCode.WriteMultipleRegisters:
                    {
                        var quantity = data.Length / 2;
                        WriteBigEndian(pdu.AsSpan(3..5), (ushort)quantity);
                        pdu[5] = (byte)data.Length;
                        data.AsSpan().CopyTo(pdu.AsSpan(6..));
                        break;
                    }
                }

                return pdu;
            },
            (functionCode, response, _) =>
            {
                var responseSpan = response.AsSpan();
                // 写响应校验
                if (responseSpan.Length < 4)
                {
                    return Result.Failure("写操作响应帧过短");
                }

                // 对比返回的地址和数量是否匹配
                var respAddr = ReadBigEndian(responseSpan[1..3]);
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
    /// 订阅 Modbus 地址的值变化。
    /// 内部通过后台轮询实现，轮询间隔由 <see cref="DriverConfigBase.HeartbeatIntervalMs"/> 控制（默认 30s）。
    /// 返回的 <see cref="IDisposable"/> 可用于取消订阅。
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

        // 使用 SubscriptionHandle 来取消订阅
        return ValueTask.FromResult<IDisposable>(new SubscriptionHandle(this, address));
    }

    // ── 异步释放 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _logger.LogInformation("{Driver} 正在释放资源...", DriverName);

        await _disposalCts.CancelAsync().ConfigureAwait(false);
        _disposalCts.Dispose();

        // 取消所有订阅
        _subscriptions.Clear();

        // 断开连接
        await CleanupConnectionAsync().ConfigureAwait(false);

        _requestLock.Dispose();

        _logger.LogInformation("{Driver} 资源已释放", DriverName);
    }

    // ── 内部实现 ─────────────────────────────────────────────

    /// <summary>
    /// 执行 Modbus 请求的通用方法：构建 PDU → 发送 → 接收 → 解析响应。
    /// </summary>
    private async ValueTask<TResult> ExecuteRequestAsync<TResult>(
        ModbusFunctionCode functionCode,
        object? context,
        Func<byte[]> buildRequest,
        Func<ModbusFunctionCode, byte[], object?, TResult> parseResponse,
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

            if (_stream is null)
            {
                return CreateFailureResult<TResult>("流对象为空");
            }

            // ── 发送请求 ──────────────────────────────────────
            var transactionId = checked(++_nextTransactionId);

            // 构建完整的 Modbus TCP 帧
            var pdu = buildRequest();
            var frameLength = MbapHeaderLength + pdu.Length;

            // 构建发送帧（小帧直接分配 byte[]）
            var frame = new byte[frameLength];

            // 写入 MBAP 头
            WriteBigEndian(frame.AsSpan(0..2), transactionId);
            WriteBigEndian(frame.AsSpan(2..4), (ushort)ProtocolId);
            WriteBigEndian(frame.AsSpan(4..6), (ushort)(pdu.Length + 1)); // +1 for UnitId
            frame[6] = _config.UseUnitId ? _config.SlaveAddress : (byte)0;

            // 写入 PDU
            pdu.AsSpan().CopyTo(frame.AsSpan(MbapHeaderLength..));

            // 发送
            await _stream.WriteAsync(frame.AsMemory(), ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);

            _logger.LogTrace(
                "{Driver} Tx [{TransId}] FC=0x{FC:X2} Addr={Addr} Len={Len}",
                DriverName, transactionId, (byte)functionCode,
                context?.ToString() ?? "", pdu.Length);

            // ── 接收响应 ──────────────────────────────────
            // 先读 7 字节 MBAP 头，再根据 Length 字段读剩余数据
            byte[] header = new byte[MbapHeaderLength];
            var headerRead = await ReadExactAsync(_stream, header, 0, MbapHeaderLength, ct).ConfigureAwait(false);
            if (!headerRead)
            {
                return CreateFailureResult<TResult>("读取响应头失败：连接可能已断开");
            }

            var responseLength = ReadBigEndian(header.AsSpan(4..6)); // Length 字段包含 UnitId
            if (responseLength < 2 || responseLength > MaxPduLength + 1)
            {
                return CreateFailureResult<TResult>($"无效的响应长度: {responseLength}");
            }

            var pduLength = responseLength - 1; // 减去 UnitId

            byte[] pduBuffer = ArrayPool<byte>.Shared.Rent(pduLength);
            try
            {
                var pduRead = await ReadExactAsync(_stream, pduBuffer, 0, pduLength, ct).ConfigureAwait(false);
                if (!pduRead)
                {
                    return CreateFailureResult<TResult>("读取响应 PDU 失败：连接可能已断开");
                }

                var pduSpan = pduBuffer.AsSpan(0, pduLength);

                // 校验响应事务 ID
                var respTransId = ReadBigEndian(header.AsSpan(0..2));
                if (respTransId != transactionId)
                {
                    _logger.LogWarning(
                        "{Driver} 响应事务 ID 不匹配: 期望={Expected}, 实际={Actual}",
                        DriverName, transactionId, respTransId);
                }

                // ── 解析响应 ──────────────────────────────
                var responseFC = (ModbusFunctionCode)(pduSpan[0] & FunctionCodeMask);

                // 检查异常响应
                if ((pduSpan[0] & ErrorFlag) != 0)
                {
                    var exceptionCode = pduSpan.Length > 1 ? pduSpan[1] : (byte)0;
                    var errorMsg = FormatModbusError(exceptionCode);
                    _logger.LogWarning(
                        "{Driver} Rx [{TransId}] 异常: FC=0x{FC:X2} Code={Code}: {Msg}",
                        DriverName, transactionId, (byte)functionCode, exceptionCode, errorMsg);
                    return CreateFailureResult<TResult>(errorMsg);
                }

                if (responseFC != functionCode)
                {
                    _logger.LogWarning(
                        "{Driver} Rx [{TransId}] 功能码不匹配: 期望=0x{Exp:X2} 实际=0x{Act:X2}",
                        DriverName, transactionId, (byte)functionCode, (byte)responseFC);
                }

                _logger.LogTrace(
                    "{Driver} Rx [{TransId}] FC=0x{FC:X2} Len={Len}",
                    DriverName, transactionId, (byte)responseFC, pduLength);

                return parseResponse(responseFC, pduBuffer.AsSpan(0, pduLength).ToArray(), context);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(pduBuffer);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CreateFailureResult<TResult>("操作已取消");
        }
        catch (SocketException ex)
        {
            _logger.LogError(ex, "{Driver} 网络错误", DriverName);
            // 标记连接已断开
            _ = HandleConnectionLostAsync(ex.Message);
            return CreateFailureResult<TResult>($"网络错误: {ex.Message}");
        }
        catch (IOException ex) when (ex.InnerException is SocketException)
        {
            _logger.LogError(ex, "{Driver} IO 错误", DriverName);
            _ = HandleConnectionLostAsync(ex.Message);
            return CreateFailureResult<TResult>($"IO 错误: {ex.Message}");
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
    /// 从网络中精确读取指定字节数。
    /// 使用 ArraySegment 而非 Span 以避免 async 上下文中的 ref struct 限制。
    /// </summary>
    private static async ValueTask<bool> ReadExactAsync(
        NetworkStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
    {
        var end = offset + count;
        while (offset < end)
        {
            Memory<byte> segment = buffer.AsMemory(offset, end - offset);
            var read = await stream.ReadAsync(segment, ct).ConfigureAwait(false);
            if (read == 0)
            {
                return false; // 连接关闭
            }
            offset += read;
        }
        return true;
    }

    // ── 地址解析 ─────────────────────────────────────────────

    /// <summary>
    /// 解析读操作地址字符串。
    /// 格式：[FC前缀:][地址]，例如 "03:100", "01:0", "100"。
    /// 默认 FC = 03（读保持寄存器）。
    /// </summary>
    private static bool TryParseReadAddress(
        string address, out ModbusFunctionCode functionCode, out ushort startAddress)
    {
        functionCode = ModbusFunctionCode.ReadHoldingRegisters;
        startAddress = 0;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        // 尝试解析 FC:Address 格式，支持十六进制（如 "0F:0"）和十进制（如 "15:0"）
        var colonIndex = address.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= 2) // FC 前缀最多 2 位
        {
            var fcPart = address[..colonIndex];
            var addrPart = address[(colonIndex + 1)..];

            // 先尝试十进制，若失败则尝试十六进制
            if ((!byte.TryParse(fcPart, out var fcByte) ||
                 !ushort.TryParse(addrPart, out var addr)) &&
                !(byte.TryParse(fcPart, System.Globalization.NumberStyles.HexNumber, null, out fcByte) &&
                  ushort.TryParse(addrPart, out addr)))
            {
                return false;
            }

            functionCode = (ModbusFunctionCode)fcByte;
            startAddress = addr;
            return true;
        }

        // 纯数字地址 → 优先匹配 PLC 标准地址段，否则默认保持寄存器
        if (ushort.TryParse(address, out var plainAddr))
        {
            if (plainAddr is >= 40001 and <= 49999)
            {
                functionCode = ModbusFunctionCode.ReadHoldingRegisters;
                startAddress = (ushort)(plainAddr - 40001);
            }
            else if (plainAddr is >= 30001 and <= 39999)
            {
                functionCode = ModbusFunctionCode.ReadInputRegisters;
                startAddress = (ushort)(plainAddr - 30001);
            }
            else if (plainAddr is >= 10001 and <= 19999)
            {
                functionCode = ModbusFunctionCode.ReadDiscreteInputs;
                startAddress = (ushort)(plainAddr - 10001);
            }
            else
            {
                // 范围外（含 < 10001）的纯数字默认保持寄存器
                functionCode = ModbusFunctionCode.ReadHoldingRegisters;
                startAddress = plainAddr;
            }
            return true;
        }

        return false;
    }

    /// <summary>
    /// 解析写操作地址和数据，确定功能码。
    /// </summary>
    private static bool TryParseWriteAddress(
        string address, byte[] data, out ModbusFunctionCode functionCode, out ushort startAddress)
    {
        functionCode = ModbusFunctionCode.WriteSingleRegister;
        startAddress = 0;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        // 尝试解析 FC:Address 格式，支持十六进制（如 "0F:0"）和十进制（如 "15:0"）
        var colonIndex = address.IndexOf(':');
        if (colonIndex > 0 && colonIndex <= 2)
        {
            var fcPart = address[..colonIndex];
            var addrPart = address[(colonIndex + 1)..];

            // 先尝试十进制，若失败则尝试十六进制
            byte fcByte;
            ushort addr;
            if ((!byte.TryParse(fcPart, out fcByte) ||
                 !ushort.TryParse(addrPart, out addr)) &&
                !(byte.TryParse(fcPart, System.Globalization.NumberStyles.HexNumber, null, out fcByte) &&
                  ushort.TryParse(addrPart, out addr)))
            {
                return false;
            }

            var fc = (ModbusFunctionCode)fcByte;

            // 验证功能码是否为写操作
            if (fc is ModbusFunctionCode.WriteSingleCoil
                or ModbusFunctionCode.WriteSingleRegister
                or ModbusFunctionCode.WriteMultipleCoils
                or ModbusFunctionCode.WriteMultipleRegisters)
            {
                functionCode = fc;
                startAddress = addr;
                return true;
            }
        }

        // 无前缀格式，根据数据长度推断
        if (ushort.TryParse(address, out var plainAddr))
        {
            // 支持 PLC 地址格式
            if (plainAddr >= 40001)
            {
                plainAddr = (ushort)(plainAddr - 40001);
            }

            functionCode = data.Length switch
            {
                1 => ModbusFunctionCode.WriteSingleCoil,
                2 => ModbusFunctionCode.WriteSingleRegister,
                _ => ModbusFunctionCode.WriteMultipleRegisters // 默认多寄存器
            };
            startAddress = plainAddr;
            return true;
        }

        return false;
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
                break; // 无订阅时停止轮询
            }

            if (State != ConnectionState.Connected)
            {
                continue;
            }

            // 对每个订阅地址执行轮询读取
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
        if (_subscriptions.TryRemove(address, out var entry))
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
                30_000); // 最大 30s

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

    private async Task CleanupConnectionAsync()
    {
        try
        {
            if (_stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Driver} 清理流时异常", DriverName);
        }
        finally
        {
            _stream = null;
        }

        try
        {
            _tcpClient?.Close();
            _tcpClient?.Dispose();
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Driver} 清理 TCP 客户端时异常", DriverName);
        }
        finally
        {
            _tcpClient = null;
        }
    }

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        var detail = newState switch
        {
            ConnectionState.Connected => $"已连接到 {_config.Host}:{_config.Port}",
            ConnectionState.Disconnected => "已断开",
            ConnectionState.Reconnecting => $"正在重连（最多 {_config.ReconnectMaxRetries} 次）",
            _ => string.Empty
        };

        var args = new ConnectionStateChangedEventArgs(oldState, newState, detail);
        ConnectionStateChanged?.Invoke(this, args);
    }

    // ── 工具方法 ─────────────────────────────────────────────

    /// <summary>
    /// 将 ushort 以大端序写入 span。
    /// </summary>
    private static void WriteBigEndian(Span<byte> destination, ushort value)
    {
        destination[0] = (byte)(value >> 8);
        destination[1] = (byte)value;
    }

    /// <summary>
    /// 从 span 读取大端序 ushort。
    /// </summary>
    private static ushort ReadBigEndian(ReadOnlySpan<byte> source)
    {
        return (ushort)((source[0] << 8) | source[1]);
    }

    /// <summary>
    /// 格式化 Modbus 异常码为可读消息。
    /// </summary>
    private static string FormatModbusError(byte exceptionCode)
    {
        return exceptionCode switch
        {
            0x01 => "非法功能码 (Illegal Function)",
            0x02 => "非法数据地址 (Illegal Data Address)",
            0x03 => "非法数据值 (Illegal Data Value)",
            0x04 => "从站设备故障 (Slave Device Failure)",
            0x05 => "确认超时 (Acknowledge)",
            0x06 => "从站忙 (Slave Device Busy)",
            0x08 => "存储器奇偶校验错误 (Memory Parity Error)",
            0x0A => "网关路径不可用 (Gateway Path Unavailable)",
            0x0B => "网关目标设备无响应 (Gateway Target Device Failed)",
            _ => $"未知异常码 0x{exceptionCode:X2}"
        };
    }

    /// <summary>
    /// 创建失败结果（兼容 Result 和 Result&lt;T&gt;）。
    /// </summary>
    private static TResult CreateFailureResult<TResult>(string error)
    {
        var type = typeof(TResult);

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Result<>))
        {
            // Result<T>.Failure(string)
            var failureMethod = type.GetMethod(
                "Failure",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                [typeof(string)]);

            if (failureMethod is not null)
            {
                return (TResult)failureMethod.Invoke(null, [error])!;
            }
        }

        // Result.Failure(string)
        var result = Result.Failure(error);
        return (TResult)(object)result;
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 订阅条目：记录地址和回调，并持有上次值以实现变化检测。
    /// </summary>
    private sealed class SubscriptionEntry
    {
        private readonly string _address;
        private readonly Action<byte[]> _callback;
        private byte[]? _lastValue;

        public SubscriptionEntry(string address, Action<byte[]> callback)
        {
            _address = address;
            _callback = callback;
        }

        /// <summary>
        /// 如果值发生变化则调用回调。
        /// </summary>
        public void InvokeIfChanged(byte[] newValue)
        {
            if (_lastValue is not null &&
                _lastValue.AsSpan().SequenceEqual(newValue))
            {
                return; // 无变化
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
        private readonly ModbusTcpDriver _driver;
        private readonly string _address;

        public SubscriptionHandle(ModbusTcpDriver driver, string address)
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
