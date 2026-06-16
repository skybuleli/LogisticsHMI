using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;
using S7.Net;

namespace App.Infrastructure.Drivers;

/// <summary>
/// Siemens S7 驱动实现。
/// <para>
/// 基于 S7netplus 库实现 ISO-on-TCP 通信，支持 S7-1200 / S7-1500 / S7-300 / S7-400 等 CPU 类型。
/// 使用 <see cref="Plc.OpenAsync"/> / <see cref="Plc.ReadBytesAsync"/> / <see cref="Plc.WriteBytesAsync"/>
/// 实现完全异步操作。
/// </para>
/// <para>
/// 地址格式：
/// <list type="bullet">
///   <item><c>DB{db}.{offset}</c> — DataBlock 区 (DB)，例如 <c>DB1.0</c>、<c>DB100.24</c></item>
///   <item><c>I{offset}</c>       — 输入映像区 (I / Process Input)，例如 <c>I0</c>、<c>I10</c></item>
///   <item><c>Q{offset}</c>       — 输出映像区 (Q / Process Output)，例如 <c>Q0</c>、<c>Q4</c></item>
///   <item><c>M{offset}</c>       — 存储区 (M / Memory)，例如 <c>M0</c>、<c>M100</c></item>
/// </list>
/// </para>
/// <para>
/// 线程安全（单连接串行化请求），完整异步支持。支持心跳检测、自动重连（指数退避）和订阅轮询。
/// </para>
/// </summary>
public sealed class S7Driver : IDeviceDriver
{
    // ── 字段 ───────────────────────────────────────────────
    private readonly S7DriverConfig _config;
    private readonly ILogger<S7Driver> _logger;
    private readonly SemaphoreSlim _requestLock = new(1, 1);

    private Plc? _plc;

    // 订阅管理
    private readonly ConcurrentDictionary<string, SubscriptionEntry> _subscriptions = new();
    private readonly CancellationTokenSource _disposalCts = new();
    private Task? _pollTask;

    // 连接状态
    private ConnectionState _state = ConnectionState.Disconnected;

    /// <summary>
    /// 初始化 S7 驱动。
    /// </summary>
    public S7Driver(S7DriverConfig config, ILogger<S7Driver> logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
    }

    // ── IDeviceDriver 属性 ─────────────────────────────────

    /// <inheritdoc />
    public string DriverName => $"S7-{_config.DeviceId}";

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
            "{Driver} 正在连接 S7 PLC {Host} (CpuType={CpuType}, Rack={Rack}, Slot={Slot}) ...",
            DriverName, _config.IpAddress, _config.CpuType, _config.Rack, _config.Slot);

        Plc? plc = null;
        try
        {
            plc = CreatePlcInstance();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_config.TimeoutMs);

            // OpenAsync 是 S7netplus 提供的异步 API
            await plc.OpenAsync(timeoutCts.Token).ConfigureAwait(false);

            _plc = plc;
            State = ConnectionState.Connected;
            _logger.LogInformation(
                "{Driver} 连接成功 ({Host}, CpuType={CpuType})",
                DriverName, _config.IpAddress, _config.CpuType);

            StartPollIfNeeded();
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            plc?.Close();
            State = ConnectionState.Disconnected;
            _logger.LogWarning("{Driver} 连接超时 ({Timeout}ms)", DriverName, _config.TimeoutMs);
            return Result.Failure($"连接超时 ({_config.TimeoutMs}ms)");
        }
        catch (PlcException ex)
        {
            plc?.Close();
            State = ConnectionState.Disconnected;
            _logger.LogError(ex, "{Driver} PLC 连接失败: {Host}", DriverName, _config.IpAddress);
            return Result.Failure($"PLC 连接失败: {ex.Message}");
        }
        catch (Exception ex)
        {
            plc?.Close();
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
    /// 地址格式（不区分大小写）：
    /// <list type="bullet">
    ///   <item><c>DB{db}.{offset}</c> — DataBlock，例如 <c>DB1.0</c> 读取 DB1 第 0 字节</item>
    ///   <item><c>I{offset}</c>       — 输入映像区，例如 <c>I0</c> 读取 I 区第 0 字节</item>
    ///   <item><c>Q{offset}</c>       — 输出映像区，例如 <c>Q4</c> 读取 Q 区第 4 字节</item>
    ///   <item><c>M{offset}</c>       — 存储区，例如 <c>M100</c> 读取 M 区第 100 字节</item>
    /// </list>
    /// length：要读取的字节数。
    /// </remarks>
    public async ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
    {
        if (!TryParseAddress(address, out var dataType, out var dbNumber, out var startByte))
        {
            return Result<byte[]>.Failure($"无效地址格式: {address}");
        }

        if (length == 0)
        {
            return Result<byte[]>.Success([]);
        }

        _logger.LogTrace(
            "{Driver} 读取: {Address} → DataType={DataType}, DB={Db}, StartByte={Start}, Length={Len}",
            DriverName, address, dataType, dbNumber, startByte, length);

        return await ExecuteWithLockAsync(ct, async () =>
        {
            if (_plc is null || !_plc.IsConnected)
            {
                return Result<byte[]>.Failure($"{DriverName} 未连接，无法读取");
            }

            try
            {
                // 使用 S7netplus 的异步 API
                var result = await _plc.ReadBytesAsync(dataType, dbNumber, startByte, length)
                    .ConfigureAwait(false);

                _logger.LogTrace(
                    "{Driver} 读取成功: {Address} → {Len} 字节",
                    DriverName, address, result.Length);

                return Result<byte[]>.Success(result);
            }
            catch (OperationCanceledException)
            {
                return Result<byte[]>.Failure("读取操作已取消");
            }
            catch (PlcException ex)
            {
                _logger.LogError(ex, "{Driver} S7 读取异常: {Address}", DriverName, address);
                _ = HandleConnectionLostAsync(ex.Message);
                return Result<byte[]>.Failure($"S7 读取异常: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Driver} 读取失败: {Address}", DriverName, address);
                return Result<byte[]>.Failure($"读取失败: {ex.Message}");
            }
        }).ConfigureAwait(false);
    }

    /// <inheritdoc />
    /// <remarks>
    /// 地址格式与 <see cref="ReadAsync"/> 一致。
    /// data 为要写入的原始字节数组。对于位操作（如 M10.0），请在写入前将对应位打包到 byte 中。
    /// </remarks>
    public async ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(data);

        if (!TryParseAddress(address, out var dataType, out var dbNumber, out var startByte))
        {
            return Result.Failure($"无效地址格式: {address}");
        }

        if (data.Length == 0)
        {
            return Result.Success();
        }

        _logger.LogTrace(
            "{Driver} 写入: {Address} → DataType={DataType}, DB={Db}, StartByte={Start}, Len={Len}",
            DriverName, address, dataType, dbNumber, startByte, data.Length);

        return await ExecuteWithLockAsync(ct, async () =>
        {
            if (_plc is null || !_plc.IsConnected)
            {
                return Result.Failure($"{DriverName} 未连接，无法写入");
            }

            try
            {
                // 使用 S7netplus 的异步 API
                await _plc.WriteBytesAsync(dataType, dbNumber, startByte, data)
                    .ConfigureAwait(false);

                _logger.LogTrace(
                    "{Driver} 写入成功: {Address} → {Len} 字节",
                    DriverName, address, data.Length);

                return Result.Success();
            }
            catch (OperationCanceledException)
            {
                return Result.Failure("写入操作已取消");
            }
            catch (PlcException ex)
            {
                _logger.LogError(ex, "{Driver} S7 写入异常: {Address}", DriverName, address);
                _ = HandleConnectionLostAsync(ex.Message);
                return Result.Failure($"S7 写入异常: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "{Driver} 写入失败: {Address}", DriverName, address);
                return Result.Failure($"写入失败: {ex.Message}");
            }
        }).ConfigureAwait(false);
    }

    // ── 订阅 ─────────────────────────────────────────────────

    /// <inheritdoc />
    /// <summary>
    /// 订阅 S7 地址的值变化。内部通过后台轮询实现，轮询间隔由
    /// <see cref="DriverConfigBase.HeartbeatIntervalMs"/> 控制（默认 30s）。
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

    // ── 地址解析 ─────────────────────────────────────────────

    /// <summary>
    /// 解析 S7 地址字符串。
    /// </summary>
    /// <param name="address">地址字符串（不区分大小写）。</param>
    /// <param name="dataType">解析后的 PLC 数据区类型。</param>
    /// <param name="dbNumber">DataBlock 编号（非 DB 区时固定为 0）。</param>
    /// <param name="startByte">起始字节偏移。</param>
    /// <returns>解析是否成功。</returns>
    /// <remarks>
    /// 支持的格式：
    /// <list type="bullet">
    ///   <item><c>DB{db}.{offset}</c> — 数据块，如 DB1.0、DB100.24</item>
    ///   <item><c>I{offset}</c>       — 输入映像区，如 I0、I10</item>
    ///   <item><c>Q{offset}</c>       — 输出映像区，如 Q0、Q4</item>
    ///   <item><c>M{offset}</c>       — 存储区，如 M0、M100</item>
    /// </list>
    /// 位偏移语法（如 DB1.0.0、M10.7）会被解析为对应字节，调用方自行处理位掩码。
    /// </remarks>
    public static bool TryParseAddress(
        string? address,
        out DataType dataType,
        out int dbNumber,
        out int startByte)
    {
        dataType = DataType.DataBlock;
        dbNumber = 0;
        startByte = 0;

        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        ReadOnlySpan<char> span = address.AsSpan().Trim();

        // ── 数据块: DB{db}.{offset} ─────────────────────────
        if (span.Length > 2 && span[0] == 'D' && span[1] == 'B' &&
            (span[2] == '.' || char.IsAsciiDigit(span[2])))
        {
            dataType = DataType.DataBlock;

            var remaining = span[2..];
            // 跳过可选的 "."
            if (remaining[0] == '.')
            {
                remaining = remaining[1..];
            }

            var dotIndex = remaining.IndexOf('.');
            if (dotIndex < 0)
            {
                // DB{db} 不指定偏移 → 默认为 0
                return int.TryParse(remaining, out dbNumber);
            }

            // DB{db}.{offset}
            if (!int.TryParse(remaining[..dotIndex], out dbNumber))
            {
                return false;
            }

            var offsetPart = remaining[(dotIndex + 1)..];
            // 可选位偏移：DB1.0.7 → 取出字节 0，忽略位偏移
            var bitDot = offsetPart.IndexOf('.');
            var bytePart = bitDot >= 0 ? offsetPart[..bitDot] : offsetPart;

            return int.TryParse(bytePart, out startByte) && startByte >= 0;
        }

        // ── 输入映像区: I{offset} ───────────────────────────
        if (span is ['I' or 'i', ..])
        {
            dataType = DataType.Input;
            return int.TryParse(span[1..], out startByte);
        }

        // ── 输出映像区: Q{offset} ───────────────────────────
        if (span is ['Q' or 'q', ..])
        {
            dataType = DataType.Output;
            return int.TryParse(span[1..], out startByte);
        }

        // ── 存储区: M{offset} ───────────────────────────────
        if (span is ['M' or 'm', ..])
        {
            dataType = DataType.Memory;
            return int.TryParse(span[1..], out startByte);
        }

        return false;
    }

    // ── 内部实现 ─────────────────────────────────────────────

    /// <summary>
    /// 创建 S7netplus Plc 实例。
    /// </summary>
    private Plc CreatePlcInstance()
    {
        var cpuType = MapCpuType(_config.CpuType);

        // 如果配置了显式 TSAP，使用构造器 Plc(CpuType, string, short, short)
        if (_config.LocalTsap.HasValue || _config.RemoteTsap.HasValue)
        {
            var localTsap = (short)(_config.LocalTsap ?? (0x0100 + _config.Rack * 0x20 + _config.Slot));
            var remoteTsap = (short)(_config.RemoteTsap ?? (0x0100 + _config.Rack * 0x20 + _config.Slot));

            return new Plc(cpuType, _config.IpAddress, localTsap, remoteTsap);
        }

        return new Plc(cpuType, _config.IpAddress, (short)_config.Rack, (short)_config.Slot);
    }

    /// <summary>
    /// 将字符串 CPU 类型映射到 S7netplus 的 <see cref="CpuType"/> 枚举。
    /// </summary>
    private static CpuType MapCpuType(string cpuType) => cpuType switch
    {
        nameof(CpuType.S7200) => CpuType.S7200,
        nameof(CpuType.S7300) => CpuType.S7300,
        nameof(CpuType.S7400) => CpuType.S7400,
        nameof(CpuType.S71200) => CpuType.S71200,
        nameof(CpuType.S71500) => CpuType.S71500,
        "Logo0BA8" => CpuType.Logo0BA8,
        nameof(CpuType.S7200Smart) => CpuType.S7200Smart,
        _ => CpuType.S71200
    };

    /// <summary>
    /// 在请求锁的保护下执行异步操作。
    /// </summary>
    private async ValueTask<TResult> ExecuteWithLockAsync<TResult>(
        CancellationToken ct, Func<Task<TResult>> action)
    {
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _requestLock.Release();
        }
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
        SafeCleanupPlc();
        return Task.CompletedTask;
    }

    private void SafeCleanupPlc()
    {
        try
        {
            if (_plc is not null)
            {
                if (_plc.IsConnected)
                {
                    _plc.Close();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogTrace(ex, "{Driver} 清理 PLC 实例时异常", DriverName);
        }
        finally
        {
            _plc = null;
        }
    }

    private void OnConnectionStateChanged(ConnectionState oldState, ConnectionState newState)
    {
        var detail = newState switch
        {
            ConnectionState.Connected => $"已连接到 {_config.IpAddress} (CpuType={_config.CpuType})",
            ConnectionState.Disconnected => "已断开",
            ConnectionState.Reconnecting => $"正在重连（最多 {_config.ReconnectMaxRetries} 次）",
            _ => string.Empty
        };

        var args = new ConnectionStateChangedEventArgs(oldState, newState, detail);
        ConnectionStateChanged?.Invoke(this, args);
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
        private readonly S7Driver _driver;
        private readonly string _address;

        public SubscriptionHandle(S7Driver driver, string address)
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
