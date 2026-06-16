using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure;

/// <summary>
/// 设备连接池管理器的默认实现。
/// <para>
/// 功能：
/// <list type=\"bullet\">
///   <item>延迟创建驱动实例（按需创建）</item>
///   <item>全局并发限流（SemaphoreSlim）</item>
///   <item>周期性健康检查 + 自动重连</item>
///   <item>连接池统计和事件通知</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConnectionPoolManager : IDeviceConnectionPool
{
    private readonly IDeviceDriverFactory _factory;
    private readonly IConfigurationService _configService;
    private readonly ILogger<ConnectionPoolManager> _logger;

    // 驱动存储（DeviceId → PooledDriverEntry）
    private readonly ConcurrentDictionary<string, PooledDriverEntry> _drivers = new(StringComparer.OrdinalIgnoreCase);

    // 每设备创建锁（防止同一 deviceId 的并发创建竞态）
    private readonly ConcurrentDictionary<string, object> _deviceLocks = new(StringComparer.OrdinalIgnoreCase);

    // 全局并发限流
    private readonly SemaphoreSlim _connectionThrottle;

    // 健康检查
    private Timer? _healthCheckTimer;
    private CancellationTokenSource? _healthCheckCts;

    // 统计数据
    private long _totalConnects;
    private long _totalConnectFailures;
    private long _totalDisconnects;
    private HealthCheckResult? _lastHealthCheck;
    private readonly DateTime _startTime;

    private bool _disposed;
    private readonly object _lock = new();

    public event EventHandler<ConnectionPoolEventArgs>? PoolEvent;

    public ConnectionPoolManager(
        IDeviceDriverFactory factory,
        IConfigurationService configService,
        ILogger<ConnectionPoolManager> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var poolCfg = configService.Current.ConnectionPool;
        _connectionThrottle = new SemaphoreSlim(
            poolCfg.MaxConcurrentConnections,
            poolCfg.MaxConcurrentConnections);
        _startTime = DateTime.UtcNow;
    }

    // ── 驱动访问 ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<Result<IDeviceDriver>> GetDriverAsync(string deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 检查是否已有缓存的驱动
        if (_drivers.TryGetValue(deviceId, out var entry))
        {
            entry.UpdateLastUsed();
            return Result<IDeviceDriver>.Success(entry.Driver);
        }

        // 查找设备配置
        var configEntry = FindDeviceConfig(deviceId);
        if (configEntry is null)
        {
            return Result<IDeviceDriver>.Failure($"设备 '{deviceId}' 未配置");
        }

        // 限流：等待全局并发槽位
        var throttleTimeout = TimeSpan.FromMilliseconds(
            Math.Min(configEntry?.TimeoutMs ?? 5000, 30000));

        if (!await _connectionThrottle.WaitAsync(throttleTimeout, ct).ConfigureAwait(false))
        {
            return Result<IDeviceDriver>.Failure(
                $"设备 '{deviceId}' 连接超时（等待并发槽位超过 {throttleTimeout.TotalSeconds:F0} 秒）");
        }

        try
        {
            // 双重检查：可能在等待锁的时候已被其他线程创建
            if (_drivers.TryGetValue(deviceId, out entry))
            {
                entry.UpdateLastUsed();
                return Result<IDeviceDriver>.Success(entry.Driver);
            }

            // 每设备细粒度锁，防止同一 deviceId 的并发创建
            var deviceLock = _deviceLocks.GetOrAdd(deviceId, _ => new object());
            lock (deviceLock)
            {
                // 第三次检查：在设备锁内再次确认
                if (_drivers.TryGetValue(deviceId, out var existing))
                {
                    existing.UpdateLastUsed();
                    return Result<IDeviceDriver>.Success(existing.Driver);
                }

                // 通过工厂创建驱动
                _logger.LogInformation("连接池正在创建驱动: {DeviceId} ({DriverType})",
                    deviceId, configEntry!.DriverType);

                var driver = _factory.Create(configEntry);
                var poolEntry = new PooledDriverEntry(driver);

                _drivers[deviceId] = poolEntry;
                Interlocked.Increment(ref _totalConnects);

                _logger.LogInformation("连接池已创建驱动: {DeviceId} ({DriverType})",
                    deviceId, configEntry.DriverType);

                EmitEvent("driver_created", deviceId, $"驱动创建成功 ({configEntry.DriverType})");

                return Result<IDeviceDriver>.Success(driver);
            }
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalConnectFailures);
            _logger.LogError(ex, "连接池创建驱动失败: {DeviceId}", deviceId);
            return Result<IDeviceDriver>.Failure($"创建驱动 '{deviceId}' 失败: {ex.Message}");
        }
        finally
        {
            _connectionThrottle.Release();
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<IDeviceDriver> GetAllDrivers()
    {
        return _drivers.Values.Select(e => e.Driver).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> RemoveDriverAsync(string deviceId, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_drivers.TryRemove(deviceId, out var entry))
        {
            try
            {
                await entry.Driver.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放驱动 {DeviceId} 时异常", deviceId);
            }

            Interlocked.Increment(ref _totalDisconnects);
            EmitEvent("driver_removed", deviceId, "驱动已从连接池移除");
            return true;
        }

        return false;
    }

    // ── 健康检查 ─────────────────────────────────────────────

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var intervalMs = _configService.Current.ConnectionPool.HealthCheckIntervalMs;
        if (intervalMs <= 0)
        {
            _logger.LogInformation("健康检查已禁用 (IntervalMs={Interval})", intervalMs);
            return Task.CompletedTask;
        }

        lock (_lock)
        {
            _healthCheckCts?.Cancel();
            _healthCheckCts = new CancellationTokenSource();

            _healthCheckTimer = new Timer(
                async _ =>
                {
                    try
                    {
                        await CheckHealthAsync(_healthCheckCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // 停止时正常取消
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "健康检查循环异常");
                    }
                },
                null,
                TimeSpan.FromMilliseconds(intervalMs),
                TimeSpan.FromMilliseconds(intervalMs));
        }

        _logger.LogInformation("连接池健康检查已启动 (Interval={Interval}ms)", intervalMs);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        lock (_lock)
        {
            _healthCheckTimer?.Dispose();
            _healthCheckTimer = null;
            _healthCheckCts?.Cancel();
            _healthCheckCts?.Dispose();
            _healthCheckCts = null;
        }

        _logger.LogInformation("连接池健康检查已停止");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
    {
        var startTime = DateTime.UtcNow;
        var onlineIds = new List<string>();
        var offlineIds = new List<string>();
        var errorIds = new List<string>();

        // 对每个已创建的驱动进行健康检查
        var tasks = new List<Task>();

        foreach (var kvp in _drivers)
        {
            var deviceId = kvp.Key;
            var entry = kvp.Value;

            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (ct.IsCancellationRequested) return;

                    var state = entry.Driver.State;

                    if (state == ConnectionState.Connected)
                    {
                        lock (onlineIds) { onlineIds.Add(deviceId); }

                        // 标记为已检查
                        entry.MarkHealthChecked(true);
                    }
                    else if (state is ConnectionState.Connecting or ConnectionState.Reconnecting)
                    {
                        // 正在重连中，暂不处理
                        lock (onlineIds) { onlineIds.Add(deviceId + "(重连中)"); }
                    }
                    else
                    {
                        lock (offlineIds) { offlineIds.Add(deviceId); }
                        entry.MarkHealthChecked(false);

                        // 如果启用了自动重连，尝试重连
                        if (entry.Driver.Config.AutoReconnect && entry.CanRetryReconnect())
                        {
                            _logger.LogWarning(
                                "健康检查发现设备离线 {DeviceId}({State})，尝试重连 (尝试#{Retry})",
                                deviceId, state, entry.ReconnectRetryCount + 1);

                            var connectResult = await entry.Driver
                                .ConnectAsync(ct)
                                .ConfigureAwait(false);

                            if (connectResult.IsSuccess)
                            {
                                entry.RecordReconnectSuccess();
                                _logger.LogInformation("设备 {DeviceId} 重连成功", deviceId);
                                EmitEvent("reconnect_success", deviceId, "健康检查自动重连成功");
                            }
                            else
                            {
                                entry.RecordReconnectFailure();
                                _logger.LogWarning("设备 {DeviceId} 重连失败: {Error}",
                                    deviceId, connectResult.Error);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // 忽略
                }
                catch (Exception ex)
                {
                    lock (errorIds) { errorIds.Add(deviceId); }
                    _logger.LogWarning(ex, "健康检查设备 {DeviceId} 时异常", deviceId);
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var result = new HealthCheckResult
        {
            Timestamp = DateTime.UtcNow,
            OnlineCount = onlineIds.Count,
            OfflineCount = offlineIds.Count,
            ErrorCount = errorIds.Count,
            Duration = DateTime.UtcNow - startTime,
            OfflineDeviceIds = offlineIds,
            ErrorDeviceIds = errorIds
        };

        _lastHealthCheck = result;

        if (result.AllOnline && _drivers.Count > 0)
        {
            _logger.LogDebug("健康检查完成: {Online}/{Total} 在线 ({Duration}ms)",
                result.OnlineCount, _drivers.Count, result.Duration.TotalMilliseconds);
        }
        else if (!result.AllOnline)
        {
            _logger.LogWarning("健康检查完成: {Online} 在线, {Offline} 离线, {Errors} 异常 ({Duration}ms)",
                result.OnlineCount, result.OfflineCount, result.ErrorCount, result.Duration.TotalMilliseconds);

            EmitEvent("health_check_warning", null,
                $"健康检查: {result.OnlineCount}在线/{result.OfflineCount}离线/{result.ErrorCount}异常");
        }

        return result;
    }

    // ── 统计 ─────────────────────────────────────────────────

    /// <inheritdoc />
    public ConnectionPoolStats GetStats()
    {
        return new ConnectionPoolStats
        {
            ActiveDriverCount = _drivers.Count,
            TotalConnects = Interlocked.Read(ref _totalConnects),
            TotalConnectFailures = Interlocked.Read(ref _totalConnectFailures),
            TotalDisconnects = Interlocked.Read(ref _totalDisconnects),
            PendingOperations = _connectionThrottle.CurrentCount,
            Uptime = DateTime.UtcNow - _startTime,
            LastHealthCheck = _lastHealthCheck
        };
    }

    // ── 释放 ─────────────────────────────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        _logger.LogInformation("正在释放连接池...");

        await StopAsync().ConfigureAwait(false);

        // 释放所有驱动
        foreach (var kvp in _drivers)
        {
            try
            {
                await kvp.Value.Driver.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "释放驱动 {DeviceId} 时异常", kvp.Key);
            }
        }

        _drivers.Clear();
        _connectionThrottle.Dispose();

        _logger.LogInformation("连接池已释放 (共 {Count} 个驱动)", _drivers.Count);
    }

    // ── 内部方法 ─────────────────────────────────────────────

    private DeviceConfigEntry? FindDeviceConfig(string deviceId)
    {
        return _configService.Current.Devices
            .FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
    }

    private void EmitEvent(string eventType, string? deviceId, string detail)
    {
        try
        {
            PoolEvent?.Invoke(this, new ConnectionPoolEventArgs(eventType, deviceId, detail));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "连接池事件处理器异常");
        }
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 池中的驱动条目，包含元数据。
    /// </summary>
    private sealed class PooledDriverEntry
    {
        private static readonly TimeSpan[] ReconnectBackoff =
        [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(4),
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(30)
        ];

        public PooledDriverEntry(IDeviceDriver driver)
        {
            Driver = driver ?? throw new ArgumentNullException(nameof(driver));
            CreatedAt = DateTime.UtcNow;
            LastUsedAt = DateTime.UtcNow;
        }

        public IDeviceDriver Driver { get; }
        public DateTime CreatedAt { get; }
        public DateTime LastUsedAt { get; private set; }
        public int ReconnectRetryCount { get; private set; }
        public DateTime? LastHealthCheckAt { get; private set; }
        public bool LastHealthCheckPassed { get; private set; }

        public void UpdateLastUsed() => LastUsedAt = DateTime.UtcNow;

        public void MarkHealthChecked(bool passed)
        {
            LastHealthCheckAt = DateTime.UtcNow;
            LastHealthCheckPassed = passed;
            if (passed) ReconnectRetryCount = 0;
        }

        public bool CanRetryReconnect()
        {
            if (ReconnectRetryCount >= ReconnectBackoff.Length)
            {
                return false;
            }

            // 检查退避时间
            var backoff = ReconnectBackoff[ReconnectRetryCount];
            if (LastHealthCheckAt.HasValue)
            {
                var elapsed = DateTime.UtcNow - LastHealthCheckAt.Value;
                if (elapsed < backoff)
                {
                    return false;
                }
            }

            return true;
        }

        public void RecordReconnectSuccess()
        {
            ReconnectRetryCount = 0;
            LastHealthCheckPassed = true;
        }

        public void RecordReconnectFailure()
        {
            ReconnectRetryCount++;
            LastHealthCheckPassed = false;
        }
    }
}
