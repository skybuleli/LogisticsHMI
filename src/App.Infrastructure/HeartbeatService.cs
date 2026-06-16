using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure;

/// <summary>
/// 心跳服务默认实现。
/// <para>
/// 通过定期检查 <see cref="IDeviceConnectionPool"/> 中各驱动连接状态，
/// 为每台设备维护心跳生命线状态，并在丢失/恢复时发出事件通知。
/// 不执行 I/O 操作 — 仅检查 ConnectionState 属性，
/// 避免对 PLC 等设备产生不必要的轮询压力。
/// </para>
/// </summary>
public sealed class HeartbeatService : IHeartbeatService, IDisposable
{
    private readonly IDeviceConnectionPool _pool;
    private readonly IConfigurationService _configService;
    private readonly ILogger<HeartbeatService> _logger;

    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private bool _running;
    private readonly object _lock = new();

    /// <summary>
    /// 每台设备的内部心跳状态追踪。
    /// </summary>
    private readonly ConcurrentDictionary<string, DeviceHeartbeatState> _deviceStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public HeartbeatService(
        IDeviceConnectionPool pool,
        IConfigurationService configService,
        ILogger<HeartbeatService> logger)
    {
        _pool = pool ?? throw new ArgumentNullException(nameof(pool));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_lock) return _running; }
    }

    /// <inheritdoc />
    public event EventHandler<HeartbeatTickEventArgs>? HeartbeatTick;

    /// <inheritdoc />
    public event EventHandler<HeartbeatDeviceEventArgs>? HeartbeatMissed;

    /// <inheritdoc />
    public event EventHandler<HeartbeatDeviceEventArgs>? HeartbeatRecovered;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_running) return Task.CompletedTask;

            _cts?.Dispose();
            _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            var interval = _configService.Current.Heartbeat?.IntervalMs ?? 10_000;
            if (interval < 1000) interval = 1000;

            _timer = new Timer(
                async _ => await OnTimerTickAsync(_cts.Token).ConfigureAwait(false),
                null,
                interval,  // 首次延迟 = 间隔（给设备连接时间）
                interval);

            _running = true;
            _logger.LogInformation("心跳服务已启动 (Interval={Interval}ms)", interval);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        lock (_lock)
        {
            if (!_running) return Task.CompletedTask;

            _timer?.Dispose();
            _timer = null;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
            _running = false;

            _logger.LogInformation("心跳服务已停止");
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<HeartbeatSnapshot> PulseAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var sw = ValueStopwatch.StartNew();
        var statuses = await CheckAllDevicesAsync(ct).ConfigureAwait(false);
        var elapsed = sw.Elapsed;

        RaiseHeartbeatTick(statuses);

        return ToSnapshot(statuses, elapsed);
    }

    /// <inheritdoc />
    public Task<HeartbeatSnapshot> GetSnapshotAsync(CancellationToken ct = default)
    {
        var drivers = _pool.GetAllDrivers();
        var statuses = drivers
            .Select(d => BuildDeviceStatus(d, _deviceStates.GetValueOrDefault(d.Config.DeviceId)))
            .ToList();

        var snapshot = ToSnapshot(statuses, TimeSpan.Zero);
        return Task.FromResult(snapshot);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        _cts?.Cancel();
        _cts?.Dispose();
        _deviceStates.Clear();
    }

    // ── 内部 ─────────────────────────────────────────────────

    private async Task OnTimerTickAsync(CancellationToken ct)
    {
        try
        {
            if (ct.IsCancellationRequested) return;

            var sw = ValueStopwatch.StartNew();
            var statuses = await CheckAllDevicesAsync(ct).ConfigureAwait(false);
            var elapsed = sw.Elapsed;

            _logger.LogTrace("心跳检查完成: {Online}/{Total} 在线 ({Elapsed}ms)",
                statuses.Count(s => s.IsAlive), statuses.Count, elapsed.TotalMilliseconds);

            RaiseHeartbeatTick(statuses);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "心跳检查循环异常");
        }
    }

    /// <summary>
    /// 检查所有设备的心跳状态。
    /// </summary>
    private async Task<List<HeartbeatDeviceStatus>> CheckAllDevicesAsync(CancellationToken ct)
    {
        var drivers = _pool.GetAllDrivers();
        var results = new List<HeartbeatDeviceStatus>(drivers.Count);
        var config = _configService.Current.Heartbeat ?? new HeartbeatConfig();
        var missedThreshold = config.MissedThreshold;

        foreach (var driver in drivers)
        {
            var deviceId = driver.Config.DeviceId;
            var state = _deviceStates.GetOrAdd(deviceId, _ => new DeviceHeartbeatState());

            var isAlive = driver.State == ConnectionState.Connected;
            var isNowMissed = !isAlive;

            if (isNowMissed)
            {
                state.ConsecutiveMissed++;
                state.LastMissedTime = DateTime.UtcNow;
            }
            else
            {
                // 如果之前有丢失计数，现在恢复了
                if (state.ConsecutiveMissed > 0)
                {
                    _logger.LogDebug("设备 {DeviceId} 心跳恢复 (丢失 {Count} 次)",
                        deviceId, state.ConsecutiveMissed);
                }
                state.ConsecutiveMissed = 0;
                state.LastHeartbeatTime = DateTime.UtcNow;
            }

            // 触发 Missed 事件（首次达到阈值时）
            if (isNowMissed && state.ConsecutiveMissed == missedThreshold)
            {
                state.WasMissed = true;
                state.SeenMissedCount++;

                _logger.LogWarning("设备 {DeviceId} 心跳丢失 (连续 {Count} 次)",
                    deviceId, state.ConsecutiveMissed);

                // 异步通知事件（不阻塞心跳循环）
                var missedStatus = BuildDeviceStatus(driver, state);
                HeartbeatMissed?.Invoke(this, new HeartbeatDeviceEventArgs
                {
                    DeviceId = deviceId,
                    Status = missedStatus,
                    Detail = $"心跳丢失 (连续 {state.ConsecutiveMissed} 次未响应)"
                });
            }

            // 连续超过阈值后的每次丢失都记录 Trace
            if (isNowMissed && state.ConsecutiveMissed > missedThreshold)
            {
                _logger.LogTrace("设备 {DeviceId} 持续丢失心跳 (连续 {Count} 次)",
                    deviceId, state.ConsecutiveMissed);
            }

            // 检测恢复：之前触发过 Missed，现在心跳回来了
            if (!isNowMissed && state.WasMissed)
            {
                state.WasMissed = false;
                _logger.LogInformation("设备 {DeviceId} 心跳恢复", deviceId);

                var recoveredStatus = BuildDeviceStatus(driver, state);
                HeartbeatRecovered?.Invoke(this, new HeartbeatDeviceEventArgs
                {
                    DeviceId = deviceId,
                    Status = recoveredStatus,
                    Detail = $"心跳恢复 (丢失 {state.SeenMissedCount} 次后)"
                });
                results.Add(recoveredStatus);
                continue;
            }

            results.Add(BuildDeviceStatus(driver, state));
        }

        // 清理已不在池中的设备状态
        var activeDeviceIds = new HashSet<string>(drivers.Select(d => d.Config.DeviceId), StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _deviceStates)
        {
            if (!activeDeviceIds.Contains(kvp.Key))
            {
                _deviceStates.TryRemove(kvp.Key, out _);
            }
        }

        return results;
    }

    private void RaiseHeartbeatTick(List<HeartbeatDeviceStatus> statuses)
    {
        try
        {
            HeartbeatTick?.Invoke(this, new HeartbeatTickEventArgs
            {
                Timestamp = DateTime.UtcNow,
                DeviceStatuses = statuses.AsReadOnly(),
                OnlineCount = statuses.Count(s => s.IsAlive),
                OfflineCount = statuses.Count(s => !s.IsAlive)
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "心跳 Tick 事件处理器异常");
        }
    }

    private static HeartbeatDeviceStatus BuildDeviceStatus(
        IDeviceDriver driver, DeviceHeartbeatState? state)
    {
        return new HeartbeatDeviceStatus
        {
            DeviceId = driver.Config.DeviceId,
            DriverName = driver.DriverName,
            DriverType = ExtractDriverType(driver.DriverName),
            State = driver.State,
            IsAlive = driver.State == ConnectionState.Connected
                      && (state?.ConsecutiveMissed ?? 0) == 0,
            ConsecutiveMissedCount = state?.ConsecutiveMissed ?? 0,
            LastHeartbeatLatency = null, // 纯状态检查，无延迟测量
            LastHeartbeatTime = state?.LastHeartbeatTime,
            LastMissedTime = state?.LastMissedTime,
        };
    }

    private static HeartbeatSnapshot ToSnapshot(
        List<HeartbeatDeviceStatus> statuses, TimeSpan elapsed)
    {
        return new HeartbeatSnapshot
        {
            Timestamp = DateTime.UtcNow,
            DeviceStatuses = statuses.AsReadOnly(),
            OnlineCount = statuses.Count(s => s.IsAlive),
            OfflineCount = statuses.Count(s => !s.IsAlive),
            TotalDevices = statuses.Count,
            Elapsed = elapsed
        };
    }

    private static string ExtractDriverType(string driverName)
    {
        if (string.IsNullOrWhiteSpace(driverName))
            return "Unknown";

        if (driverName.StartsWith("ModbusTCP", StringComparison.OrdinalIgnoreCase))
            return "ModbusTCP";
        if (driverName.StartsWith("ModbusRTU", StringComparison.OrdinalIgnoreCase))
            return "ModbusRTU";
        if (driverName.StartsWith("S7", StringComparison.OrdinalIgnoreCase))
            return "S7";
        if (driverName.StartsWith("OPCUA", StringComparison.OrdinalIgnoreCase) ||
            driverName.StartsWith("OpcUa", StringComparison.OrdinalIgnoreCase))
            return "OPCUA";
        if (driverName.StartsWith("MQTT", StringComparison.OrdinalIgnoreCase))
            return "MQTT";

        return "Unknown";
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 每台设备的内部心跳状态。
    /// </summary>
    private sealed class DeviceHeartbeatState
    {
        /// <summary>连续丢失心跳次数。</summary>
        public int ConsecutiveMissed;

        /// <summary>上次心跳成功时间（UTC）。</summary>
        public DateTime? LastHeartbeatTime;

        /// <summary>上次丢失心跳时间（UTC）。</summary>
        public DateTime? LastMissedTime;

        /// <summary>是否触发过 Missed 事件且尚未恢复。</summary>
        public bool WasMissed;

        /// <summary>历史上触发过的 Missed 事件计数。</summary>
        public int SeenMissedCount;
    }
}

/// <summary>
/// 轻量级高精度计时器（避免 Stopwatch 在基准测试之外的分配开销）。
/// </summary>
internal readonly struct ValueStopwatch
{
    private readonly long _startTimestamp;
    private static readonly double TimestampToTicks = TimeSpan.TicksPerSecond / (double)System.Diagnostics.Stopwatch.Frequency;

    private ValueStopwatch(long startTimestamp)
    {
        _startTimestamp = startTimestamp;
    }

    public static ValueStopwatch StartNew() => new(System.Diagnostics.Stopwatch.GetTimestamp());

    public TimeSpan Elapsed
    {
        get
        {
            var end = System.Diagnostics.Stopwatch.GetTimestamp();
            var ticks = (long)((end - _startTimestamp) * TimestampToTicks);
            return new TimeSpan(ticks);
        }
    }
}
