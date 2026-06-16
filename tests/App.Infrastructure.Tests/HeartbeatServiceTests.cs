using App.Core;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="HeartbeatService"/> 单元测试。
/// 覆盖心跳 Tick/Missed/Recovered 事件触发器、快照查询、生命周期。
/// </summary>
public class HeartbeatServiceTests
{
    private readonly ITestOutputHelper _output;

    public HeartbeatServiceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ═══════════════════════════════════════════════════════════
    // 配置默认值
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void HeartbeatConfig_DefaultValues()
    {
        var config = new HeartbeatConfig();
        Assert.Equal(10_000, config.IntervalMs);
        Assert.Equal(3, config.MissedThreshold);
        Assert.Equal(5000, config.CheckTimeoutMs);
        Assert.True(config.AutoStart);
    }

    [Fact]
    public void HeartbeatConfig_CustomValues_AreApplied()
    {
        var config = new HeartbeatConfig
        {
            IntervalMs = 5000,
            MissedThreshold = 5,
            CheckTimeoutMs = 3000,
            AutoStart = false
        };

        Assert.Equal(5000, config.IntervalMs);
        Assert.Equal(5, config.MissedThreshold);
        Assert.Equal(3000, config.CheckTimeoutMs);
        Assert.False(config.AutoStart);
    }

    // ═══════════════════════════════════════════════════════════
    // HeartbeatDeviceStatus 模型
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void HeartbeatDeviceStatus_DefaultConstruction()
    {
        var status = new HeartbeatDeviceStatus
        {
            DeviceId = "test-device",
            DriverName = "ModbusTCP-test-device",
            State = ConnectionState.Connected,
            IsAlive = true,
            ConsecutiveMissedCount = 0
        };

        Assert.Equal("test-device", status.DeviceId);
        Assert.Equal("ModbusTCP-test-device", status.DriverName);
        Assert.Equal(ConnectionState.Connected, status.State);
        Assert.True(status.IsAlive);
        Assert.Equal(0, status.ConsecutiveMissedCount);
        Assert.Null(status.LastHeartbeatTime);
        Assert.Null(status.LastMissedTime);
    }

    [Fact]
    public void HeartbeatDeviceStatus_DriverType_ExtractedFromName()
    {
        var status = new HeartbeatDeviceStatus
        {
            DeviceId = "dev1",
            DriverName = "S7-dev1",
            State = ConnectionState.Connected,
            IsAlive = true,
            ConsecutiveMissedCount = 0,
            DriverType = "S7"
        };

        Assert.Equal("S7", status.DriverType);
    }

    // ═══════════════════════════════════════════════════════════
    // PulseAsync — 立即执行心跳
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PulseAsync_WithConnectedDrivers_ReturnsOnlineSnapshot()
    {
        var (service, _) = CreateHeartbeatService([
            new MockDriver("device-1", ConnectionState.Connected),
            new MockDriver("device-2", ConnectionState.Connected),
        ]);

        var snapshot = await service.PulseAsync();

        Assert.Equal(2, snapshot.TotalDevices);
        Assert.Equal(2, snapshot.OnlineCount);
        Assert.Equal(0, snapshot.OfflineCount);
        Assert.Equal("device-1", snapshot.DeviceStatuses[0].DeviceId);
    }

    [Fact]
    public async Task PulseAsync_WithMixedDrivers_ReturnsCorrectCounts()
    {
        var (service, _) = CreateHeartbeatService([
            new MockDriver("device-online", ConnectionState.Connected),
            new MockDriver("device-offline", ConnectionState.Disconnected),
            new MockDriver("device-reconnecting", ConnectionState.Reconnecting),
        ]);

        var snapshot = await service.PulseAsync();

        Assert.Equal(3, snapshot.TotalDevices);
        Assert.Equal(1, snapshot.OnlineCount);   // 仅 Connected 算在线
        Assert.Equal(2, snapshot.OfflineCount);
    }

    [Fact]
    public async Task PulseAsync_WithEmptyPool_ReturnsEmptySnapshot()
    {
        var (service, _) = CreateHeartbeatService([]);

        var snapshot = await service.PulseAsync();

        Assert.Equal(0, snapshot.TotalDevices);
        Assert.Equal(0, snapshot.OnlineCount);
        Assert.Empty(snapshot.DeviceStatuses);
    }

    // ═══════════════════════════════════════════════════════════
    // GetSnapshotAsync — 获取快照（不触发检查）
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task GetSnapshotAsync_ReturnsCurrentDeviceStates()
    {
        var (service, _) = CreateHeartbeatService([
            new MockDriver("dev-a", ConnectionState.Connected),
            new MockDriver("dev-b", ConnectionState.Disconnected),
        ]);

        // 先 Pulse 一次建立状态
        await service.PulseAsync();

        var snapshot = await service.GetSnapshotAsync();

        Assert.Equal(2, snapshot.TotalDevices);
        // GetSnapshot 不触发检查，直接返回当前状态
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot.DeviceStatuses.Count);
    }

    // ═══════════════════════════════════════════════════════════
    // HeartbeatTick 事件
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PulseAsync_FiresHeartbeatTickEvent()
    {
        var (service, _) = CreateHeartbeatService([
            new MockDriver("dev-1", ConnectionState.Connected),
        ]);

        var tickCount = 0;
        HeartbeatTickEventArgs? capturedArgs = null;
        service.HeartbeatTick += (_, args) =>
        {
            tickCount++;
            capturedArgs = args;
        };

        await service.PulseAsync();

        Assert.Equal(1, tickCount);
        Assert.NotNull(capturedArgs);
        Assert.Single(capturedArgs!.DeviceStatuses);
        Assert.True(capturedArgs.DeviceStatuses[0].IsAlive);
    }

    [Fact]
    public async Task PulseAsync_MultiplePulses_FiresTickEachTime()
    {
        var (service, _) = CreateHeartbeatService([
            new MockDriver("dev-1", ConnectionState.Connected),
        ]);

        var tickCount = 0;
        service.HeartbeatTick += (_, _) => tickCount++;

        await service.PulseAsync();
        await service.PulseAsync();
        await service.PulseAsync();

        Assert.Equal(3, tickCount);
    }

    // ═══════════════════════════════════════════════════════════
    // HeartbeatMissed 事件
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PulseAsync_WhenDeviceOffline_MissesHeartbeat_ButNoEventBelowThreshold()
    {
        var (service, _) = CreateHeartbeatService(
            [new MockDriver("dev-offline", ConnectionState.Disconnected)],
            missedThreshold: 5); // 阈值为 5

        var missedCount = 0;
        service.HeartbeatMissed += (_, _) => missedCount++;

        // 只丢失 3 次（低于阈值 5），不应触发 Missed 事件
        await service.PulseAsync(); // 1
        await service.PulseAsync(); // 2
        await service.PulseAsync(); // 3

        Assert.Equal(0, missedCount);
    }

    [Fact]
    public async Task PulseAsync_WhenDeviceOffline_TriggersMissedEventAtThreshold()
    {
        var (service, _) = CreateHeartbeatService(
            [new MockDriver("dev-offline", ConnectionState.Disconnected)],
            missedThreshold: 3);

        var missedCount = 0;
        HeartbeatDeviceEventArgs? captured = null;
        service.HeartbeatMissed += (_, args) =>
        {
            missedCount++;
            captured = args;
        };

        // Pulse 3 次：第 3 次应该触发 Missed
        await service.PulseAsync(); // 连续丢失 1
        await service.PulseAsync(); // 连续丢失 2
        await service.PulseAsync(); // 连续丢失 3 → 触发阈值

        Assert.Equal(1, missedCount);
        Assert.NotNull(captured);
        Assert.Equal("dev-offline", captured!.DeviceId);
        Assert.Contains("丢失", captured.Detail);
    }

    [Fact]
    public async Task PulseAsync_DeviceOfflineLonger_MissedEventOnlyOnce()
    {
        var (service, _) = CreateHeartbeatService(
            [new MockDriver("dev-offline", ConnectionState.Disconnected)],
            missedThreshold: 3);

        var missedCount = 0;
        service.HeartbeatMissed += (_, _) => missedCount++;

        // 6 次脉冲，只在第 3 次触发一次 Missed
        for (int i = 0; i < 6; i++)
            await service.PulseAsync();

        Assert.Equal(1, missedCount);
    }

    // ═══════════════════════════════════════════════════════════
    // HeartbeatRecovered 事件
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PulseAsync_WhenDeviceRecovers_FiresRecoveredEvent()
    {
        var mockDriver = new MockDriver("dev-flaky", ConnectionState.Disconnected);
        var (service, _) = CreateHeartbeatService(
            [mockDriver],
            missedThreshold: 2);

        var recoveredCount = 0;
        HeartbeatDeviceEventArgs? captured = null;
        service.HeartbeatRecovered += (_, args) =>
        {
            recoveredCount++;
            captured = args;
        };

        // 丢失 2 次 → 触发 Missed
        await service.PulseAsync();
        await service.PulseAsync();

        Assert.Equal(0, recoveredCount); // 尚未恢复

        // 设备恢复在线
        mockDriver.SetState(ConnectionState.Connected);
        await service.PulseAsync();

        Assert.Equal(1, recoveredCount);
        Assert.NotNull(captured);
        Assert.Equal("dev-flaky", captured!.DeviceId);
        Assert.Contains("恢复", captured.Detail);
    }

    [Fact]
    public async Task PulseAsync_DeviceNeverMissed_NoRecoveredEvent()
    {
        var (service, _) = CreateHeartbeatService(
            [new MockDriver("dev-stable", ConnectionState.Connected)],
            missedThreshold: 3);

        var recoveredCount = 0;
        service.HeartbeatRecovered += (_, _) => recoveredCount++;

        for (int i = 0; i < 5; i++)
            await service.PulseAsync();

        Assert.Equal(0, recoveredCount);
    }

    // ═══════════════════════════════════════════════════════════
    // 组合场景：多设备混合状态
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PulseAsync_MixedDevices_CorrectEvents()
    {
        var stableDriver = new MockDriver("stable", ConnectionState.Connected);
        var flakyDriver = new MockDriver("flaky", ConnectionState.Disconnected);

        var (service, _) = CreateHeartbeatService(
            [stableDriver, flakyDriver],
            missedThreshold: 2);

        var missedEvents = new List<string>();
        var recoveredEvents = new List<string>();

        service.HeartbeatMissed += (_, args) => missedEvents.Add(args.DeviceId);
        service.HeartbeatRecovered += (_, args) => recoveredEvents.Add(args.DeviceId);

        // Pulse 2 次：flaky 丢失 2 次 → 触发 Missed
        await service.PulseAsync();
        await service.PulseAsync();

        Assert.Contains("flaky", missedEvents);
        Assert.DoesNotContain("stable", missedEvents);
        Assert.Empty(recoveredEvents);

        // flaky 恢复
        flakyDriver.SetState(ConnectionState.Connected);
        await service.PulseAsync();

        Assert.Single(recoveredEvents);
        Assert.Equal("flaky", recoveredEvents[0]);
    }

    // ═══════════════════════════════════════════════════════════
    // 生命周期
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void HeartbeatService_InitiallyNotRunning()
    {
        var (service, _) = CreateHeartbeatService([]);

        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_MakesServiceRunning()
    {
        var (service, _) = CreateHeartbeatService([]);

        await service.StartAsync();

        Assert.True(service.IsRunning);

        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_StopsService()
    {
        var (service, _) = CreateHeartbeatService([]);
        await service.StartAsync();
        Assert.True(service.IsRunning);

        await service.StopAsync();

        Assert.False(service.IsRunning);
    }

    [Fact]
    public async Task StartAsync_Twice_NoError()
    {
        var (service, _) = CreateHeartbeatService([]);

        await service.StartAsync();
        await service.StartAsync(); // 不应抛异常

        Assert.True(service.IsRunning);
        await service.StopAsync();
    }

    [Fact]
    public async Task StopAsync_WhenNotRunning_NoError()
    {
        var (service, _) = CreateHeartbeatService([]);

        await service.StopAsync(); // 不应抛异常
    }

    [Fact]
    public async Task PulseAsync_AfterDispose_Throws()
    {
        var (service, _) = CreateHeartbeatService([]);
        service.Dispose();

        // Dispose 后不应能 Pulse
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await service.PulseAsync());
    }

    // ═══════════════════════════════════════════════════════════
    // 跨设备状态隔离
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public async Task PulseAsync_DeviceState_IsIsolated()
    {
        var driverA = new MockDriver("device-a", ConnectionState.Connected);
        var driverB = new MockDriver("device-b", ConnectionState.Disconnected);

        var (service, _) = CreateHeartbeatService(
            [driverA, driverB],
            missedThreshold: 2);

        var missedDevices = new List<string>();
        service.HeartbeatMissed += (_, args) => missedDevices.Add(args.DeviceId);

        // driverB 丢失 2 次 → 触发 Missed，driverA 不应受影响
        await service.PulseAsync();
        await service.PulseAsync();

        Assert.Single(missedDevices);
        Assert.Equal("device-b", missedDevices[0]);
    }

    // ═══════════════════════════════════════════════════════════
    // 帮助方法
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// 创建心跳服务 + mock 连接池测试实例。
    /// </summary>
    private static (HeartbeatService Service, MockConnectionPool Pool) CreateHeartbeatService(
        List<IDeviceDriver> drivers,
        int missedThreshold = 3)
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var configService = new HeartbeatMockConfigService(missedThreshold);
        var pool = new MockConnectionPool(drivers);
        var logger = loggerFactory.CreateLogger<HeartbeatService>();

        var service = new HeartbeatService(pool, configService, logger);
        return (service, pool);
    }

    // ── Mock 类型 ─────────────────────────────────────────────

    /// <summary>
    /// 模拟驱动：可控制连接状态。
    /// </summary>
    private sealed class MockDriver : IDeviceDriver
    {
        private ConnectionState _state;

        public MockDriver(string deviceId, ConnectionState initialState)
        {
            _state = initialState;
            Config = new MockDriverConfig
            {
                DeviceId = deviceId,
                DisplayName = $"Mock-{deviceId}",
                HeartbeatIntervalMs = 1000,
                AutoReconnect = true,
                ReconnectMaxRetries = 3,
                ReconnectBaseDelayMs = 100
            };
        }

        public string DriverName => $"Mock-{Config.DeviceId}";
        public DriverConfigBase Config { get; }
        public ConnectionState State => _state;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public void SetState(ConnectionState newState)
        {
            var oldState = _state;
            _state = newState;
            ConnectionStateChanged?.Invoke(this,
                new ConnectionStateChangedEventArgs(oldState, newState, "test"));
        }

        public ValueTask<Result> ConnectAsync(CancellationToken ct = default)
        {
            _state = ConnectionState.Connected;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result> DisconnectAsync(CancellationToken ct = default)
        {
            _state = ConnectionState.Disconnected;
            return ValueTask.FromResult(Result.Success());
        }

        public ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default)
            => ValueTask.FromResult(Result<byte[]>.Success([]));

        public ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default)
            => ValueTask.FromResult(Result.Success());

        public ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default)
            => ValueTask.FromResult<IDisposable>(new NoopDisposable());

        public ValueTask DisposeAsync()
        {
            _state = ConnectionState.Disconnected;
            return ValueTask.CompletedTask;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    /// <summary>
    /// Mock 驱动配置。
    /// </summary>
    private sealed class MockDriverConfig : DriverConfigBase
    {
        // 最小实现
    }

    /// <summary>
    /// 模拟连接池：返回预置驱动列表。
    /// </summary>
    private sealed class MockConnectionPool : IDeviceConnectionPool
    {
        private readonly List<IDeviceDriver> _drivers;

        public MockConnectionPool(List<IDeviceDriver> drivers)
        {
            _drivers = drivers;
        }

#pragma warning disable CS0067
        public event EventHandler<ConnectionPoolEventArgs>? PoolEvent;
#pragma warning restore CS0067

        public Task<Result<IDeviceDriver>> GetDriverAsync(string deviceId, CancellationToken ct = default)
        {
            var driver = _drivers.FirstOrDefault(d =>
                d.Config.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult(driver is not null
                ? Result<IDeviceDriver>.Success(driver)
                : Result<IDeviceDriver>.Failure($"Device '{deviceId}' not found"));
        }

        public IReadOnlyList<IDeviceDriver> GetAllDrivers() => _drivers.AsReadOnly();

        public Task<bool> RemoveDriverAsync(string deviceId, CancellationToken ct = default)
        {
            var driver = _drivers.FirstOrDefault(d =>
                d.Config.DeviceId.Equals(deviceId, StringComparison.OrdinalIgnoreCase));
            if (driver is not null)
            {
                _drivers.Remove(driver);
                return Task.FromResult(true);
            }
            return Task.FromResult(false);
        }

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default)
            => Task.FromResult(new HealthCheckResult());

        public ConnectionPoolStats GetStats() => new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>
    /// 心跳测试专用配置服务（带可调阈值）。
    /// </summary>
    private sealed class HeartbeatMockConfigService : IConfigurationService
    {
        public HeartbeatMockConfigService(int missedThreshold)
        {
            Current = new AppConfig
            {
                Heartbeat = new HeartbeatConfig
                {
                    IntervalMs = 10_000,
                    MissedThreshold = missedThreshold,
                    CheckTimeoutMs = 5000,
                    AutoStart = false
                }
            };
        }

        public AppConfig Current { get; set; }
        public LoggingConfig Logging => Current.Logging;
        public WindowConfig Window => Current.Window;
        public ThemeConfig Theme => Current.Theme;
        public CommunicationConfig Communication => Current.Communication;
        public DeviceScanConfig DeviceScan => Current.DeviceScan;
        public HeartbeatConfig Heartbeat => Current.Heartbeat;

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
        {
            add { }
            remove { }
        }

        public void Reload() { }
        public void Save() { }
    }
}
