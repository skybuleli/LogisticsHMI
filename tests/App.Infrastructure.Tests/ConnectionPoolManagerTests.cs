using App.Core;
using App.Infrastructure.Drivers;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="ConnectionPoolManager"/> 单元测试。
/// 覆盖配置加载、驱动创建、健康检查、限流机制。
/// </summary>
public class ConnectionPoolManagerTests
{
    private readonly ITestOutputHelper _output;

    public ConnectionPoolManagerTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ── 模拟辅助 ─────────────────────────────────────────────

    /// <summary>
    /// 创建一个模拟的配置服务，返回指定的设备配置列表。
    /// </summary>
    private static MockConfigService CreateConfigService(List<DeviceConfigEntry>? devices = null)
    {
        return new MockConfigService(devices);
    }

    private sealed class MockConfigService : IConfigurationService
    {
        private readonly List<DeviceConfigEntry> _devices;

        public MockConfigService(List<DeviceConfigEntry>? devices)
        {
            _devices = devices ?? [];
            Current = new AppConfig
            {
                ConnectionPool = new ConnectionPoolConfig
                {
                    MaxConcurrentConnections = 10,
                    HealthCheckIntervalMs = 0, // 禁用自动健康检查
                    AutoStartHealthCheck = false
                },
                Devices = _devices
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

    /// <summary>
    /// 创建连接池管理器实例（测试用）。
    /// </summary>
    private static ConnectionPoolManager CreatePool(
        List<DeviceConfigEntry>? devices = null,
        ILoggerFactory? loggerFactory = null)
    {
        loggerFactory ??= LoggerFactory.Create(b => { });
        var configService = CreateConfigService(devices);
        var driverFactory = new DeviceDriverFactory(loggerFactory);
        var logger = loggerFactory.CreateLogger<ConnectionPoolManager>();

        return new ConnectionPoolManager(driverFactory, configService, logger);
    }

    // ── 连接池配置 ─────────────────────────────────────────────

    [Fact]
    public void ConnectionPoolConfig_DefaultValues()
    {
        var config = new ConnectionPoolConfig();
        Assert.Equal(10, config.MaxConcurrentConnections);
        Assert.Equal(30000, config.HealthCheckIntervalMs);
        Assert.True(config.AutoStartHealthCheck);
        Assert.Equal(5000, config.HealthCheckTimeoutMs);
    }

    [Fact]
    public void ConnectionPoolConfig_CustomValues_AreApplied()
    {
        var config = new ConnectionPoolConfig
        {
            MaxConcurrentConnections = 20,
            HealthCheckIntervalMs = 10000,
            AutoStartHealthCheck = false,
            HealthCheckTimeoutMs = 3000
        };

        Assert.Equal(20, config.MaxConcurrentConnections);
        Assert.Equal(10000, config.HealthCheckIntervalMs);
        Assert.False(config.AutoStartHealthCheck);
        Assert.Equal(3000, config.HealthCheckTimeoutMs);
    }

    // ── 设备配置条目 ─────────────────────────────────────────

    [Fact]
    public void DeviceConfigEntry_DefaultDriverType_IsModbusTcp()
    {
        var entry = new DeviceConfigEntry { DeviceId = "test" };
        Assert.Equal("ModbusTCP", entry.DriverType);
    }

    [Fact]
    public void DeviceConfigEntry_DefaultTimeout_Is5000()
    {
        var entry = new DeviceConfigEntry { DeviceId = "test" };
        Assert.Equal(5000, entry.TimeoutMs);
    }

    [Fact]
    public void DeviceConfigEntry_DefaultPort_Is0()
    {
        var entry = new DeviceConfigEntry { DeviceId = "test" };
        Assert.Equal(0, entry.Port);
    }

    [Fact]
    public void DeviceConfigEntry_ModbusTcpDefaults_AreSet()
    {
        var entry = new DeviceConfigEntry
        {
            DeviceId = "plc-1",
            DriverType = "ModbusTCP",
            Host = "192.168.1.100"
        };

        Assert.Equal("plc-1", entry.DeviceId);
        Assert.Equal("ModbusTCP", entry.DriverType);
        Assert.Equal("192.168.1.100", entry.Host);
        Assert.Equal((byte)1, entry.SlaveAddress);
        Assert.True(entry.UseUnitId);
    }

    [Fact]
    public void DeviceConfigEntry_MqttDefaults_AreSet()
    {
        var entry = new DeviceConfigEntry
        {
            DeviceId = "mqtt-1",
            DriverType = "MQTT",
            Broker = "mqtt.example.com"
        };

        Assert.Equal("mqtt-1", entry.DeviceId);
        Assert.Equal("MQTT", entry.DriverType);
        Assert.Equal("mqtt.example.com", entry.Broker);
        Assert.Equal(1, entry.Qos);
        Assert.False(entry.UseTls);
    }

    [Fact]
    public void DeviceConfigEntry_S7Defaults_AreSet()
    {
        var entry = new DeviceConfigEntry
        {
            DeviceId = "s7-plc",
            DriverType = "S7",
            IpAddress = "192.168.0.1"
        };

        Assert.Equal("S7", entry.DriverType);
        Assert.Equal("S71200", entry.CpuType);
        Assert.Equal(0, entry.Rack);
        Assert.Equal(2, entry.Slot);
    }

    [Fact]
    public void DeviceConfigEntry_OpcUaDefaults_AreSet()
    {
        var entry = new DeviceConfigEntry
        {
            DeviceId = "opcua-1",
            DriverType = "OPCUA",
            ServerUrl = "opc.tcp://localhost:4840"
        };

        Assert.Equal("OPCUA", entry.DriverType);
        Assert.Equal("opc.tcp://localhost:4840", entry.ServerUrl);
        Assert.Equal("None", entry.SecurityPolicy);
    }

    // ── 设备驱动工厂 ─────────────────────────────────────────

    [Fact]
    public void DeviceDriverFactory_CreateModbusTcp_ReturnsModbusTcpDriver()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var factory = new DeviceDriverFactory(loggerFactory);

        var config = new DeviceConfigEntry
        {
            DeviceId = "plc-1",
            DriverType = "ModbusTCP",
            Host = "192.168.1.100"
        };

        var driver = factory.Create(config);
        Assert.IsType<ModbusTcpDriver>(driver);
        Assert.Equal("ModbusTCP-plc-1", driver.DriverName);
        Assert.IsType<ModbusDriverConfig>(driver.Config);
    }

    [Fact]
    public void DeviceDriverFactory_CreateMqtt_ReturnsMqttDriver()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var factory = new DeviceDriverFactory(loggerFactory);

        var config = new DeviceConfigEntry
        {
            DeviceId = "mqtt-1",
            DriverType = "MQTT",
            Broker = "mqtt.example.com",
            Port = 1883
        };

        var driver = factory.Create(config);
        Assert.IsType<MqttDriver>(driver);
        Assert.Equal("MQTT-mqtt-1", driver.DriverName);
        Assert.IsType<MqttDriverConfig>(driver.Config);
    }

    [Fact]
    public void DeviceDriverFactory_CreateS7_ReturnsS7Driver()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var factory = new DeviceDriverFactory(loggerFactory);

        var config = new DeviceConfigEntry
        {
            DeviceId = "s7-plc",
            DriverType = "S7",
            IpAddress = "192.168.0.1"
        };

        var driver = factory.Create(config);
        Assert.IsType<S7Driver>(driver);
        Assert.Equal("S7-s7-plc", driver.DriverName);
        Assert.IsType<S7DriverConfig>(driver.Config);
    }

    [Fact]
    public void DeviceDriverFactory_CreateOpcUa_ReturnsOpcUaDriver()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var factory = new DeviceDriverFactory(loggerFactory);

        var config = new DeviceConfigEntry
        {
            DeviceId = "opcua-1",
            DriverType = "OPCUA",
            ServerUrl = "opc.tcp://localhost:4840"
        };

        var driver = factory.Create(config);
        Assert.IsType<OpcUaDriver>(driver);
        Assert.Equal("OPCUA-opcua-1", driver.DriverName);
        Assert.IsType<OpcUaDriverConfig>(driver.Config);
    }

    [Fact]
    public void DeviceDriverFactory_CreateModbusRtu_ReturnsModbusRtuDriver()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var factory = new DeviceDriverFactory(loggerFactory);

        var config = new DeviceConfigEntry
        {
            DeviceId = "rtu-1",
            DriverType = "ModbusRTU",
            ComPort = "COM3",
            SlaveAddress = 2
        };

        var driver = factory.Create(config);
        Assert.IsType<ModbusRtuDriver>(driver);
        Assert.Equal("ModbusRTU-rtu-1", driver.DriverName);
        Assert.IsType<ModbusRtuDriverConfig>(driver.Config);
    }

    [Fact]
    public void DeviceDriverFactory_UnsupportedType_Throws()
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var factory = new DeviceDriverFactory(loggerFactory);

        var config = new DeviceConfigEntry
        {
            DeviceId = "unknown",
            DriverType = "UnknownProtocol"
        };

        var ex = Assert.Throws<ArgumentException>(() => factory.Create(config));
        Assert.Contains("UnknownProtocol", ex.Message);
    }

    // ── 连接池管理器：GetDriverAsync ──────────────────────────

    [Fact]
    public async Task GetDriverAsync_KnownDevice_ReturnsDriver()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry
            {
                DeviceId = "plc-1",
                DriverType = "ModbusTCP",
                Host = "127.0.0.1"
            }
        ]);

        var result = await pool.GetDriverAsync("plc-1");

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Value);
        Assert.Equal("ModbusTCP-plc-1", result.Value.DriverName);
    }

    [Fact]
    public async Task GetDriverAsync_UnknownDevice_ReturnsFailure()
    {
        await using var pool = CreatePool();

        var result = await pool.GetDriverAsync("unknown-device");

        Assert.False(result.IsSuccess);
        Assert.Contains("未配置", result.Error);
    }

    [Fact]
    public async Task GetDriverAsync_SameDeviceTwice_ReturnsCachedInstance()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry
            {
                DeviceId = "plc-1",
                DriverType = "ModbusTCP",
                Host = "127.0.0.1"
            }
        ]);

        var result1 = await pool.GetDriverAsync("plc-1");
        var result2 = await pool.GetDriverAsync("plc-1");

        Assert.True(result1.IsSuccess);
        Assert.True(result2.IsSuccess);
        Assert.Same(result1.Value, result2.Value); // 同一实例
    }

    [Fact]
    public async Task GetDriverAsync_MultipleDevices_ReturnsSeparateInstances()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP", Host = "127.0.0.1" },
            new DeviceConfigEntry { DeviceId = "mqtt-1", DriverType = "MQTT", Broker = "127.0.0.1" },
            new DeviceConfigEntry { DeviceId = "s7-plc", DriverType = "S7", IpAddress = "127.0.0.1" }
        ]);

        var plc = await pool.GetDriverAsync("plc-1");
        var mqtt = await pool.GetDriverAsync("mqtt-1");
        var s7 = await pool.GetDriverAsync("s7-plc");

        Assert.True(plc.IsSuccess);
        Assert.True(mqtt.IsSuccess);
        Assert.True(s7.IsSuccess);

        Assert.NotSame(plc.Value, mqtt.Value);
        Assert.IsType<ModbusTcpDriver>(plc.Value);
        Assert.IsType<MqttDriver>(mqtt.Value);
        Assert.IsType<S7Driver>(s7.Value);
    }

    // ── 连接池管理器：GetAllDrivers / RemoveDriverAsync ──────

    [Fact]
    public async Task GetAllDrivers_ReturnsAllCreatedDrivers()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP", Host = "127.0.0.1" },
            new DeviceConfigEntry { DeviceId = "mqtt-1", DriverType = "MQTT", Broker = "127.0.0.1" }
        ]);

        await pool.GetDriverAsync("plc-1");
        await pool.GetDriverAsync("mqtt-1");

        var all = pool.GetAllDrivers();
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetAllDrivers_EmptyPool_ReturnsEmptyList()
    {
        await using var pool = CreatePool();
        var all = pool.GetAllDrivers();
        Assert.Empty(all);
    }

    [Fact]
    public async Task RemoveDriverAsync_ExistingDevice_RemovesAndDisposes()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP", Host = "127.0.0.1" }
        ]);

        await pool.GetDriverAsync("plc-1");
        var removed = await pool.RemoveDriverAsync("plc-1");

        Assert.True(removed);
        Assert.Empty(pool.GetAllDrivers());
    }

    [Fact]
    public async Task RemoveDriverAsync_UnknownDevice_ReturnsFalse()
    {
        await using var pool = CreatePool();
        var removed = await pool.RemoveDriverAsync("nonexistent");
        Assert.False(removed);
    }

    // ── 连接池管理器：GetStats ────────────────────────────────

    [Fact]
    public async Task GetStats_AfterCreation_ReturnsCorrectValues()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP", Host = "127.0.0.1" },
            new DeviceConfigEntry { DeviceId = "mqtt-1", DriverType = "MQTT", Broker = "127.0.0.1" }
        ]);

        await pool.GetDriverAsync("plc-1");
        await pool.GetDriverAsync("mqtt-1");

        var stats = pool.GetStats();

        Assert.Equal(2, stats.ActiveDriverCount);
        Assert.True(stats.TotalConnects >= 2);
        Assert.Equal(0, stats.TotalConnectFailures);
        Assert.True(stats.Uptime > TimeSpan.Zero);
    }

    // ── 连接池管理器：健康检查 ─────────────────────────────────

    [Fact]
    public async Task CheckHealthAsync_EmptyPool_ReturnsAllOnline()
    {
        await using var pool = CreatePool();
        var result = await pool.CheckHealthAsync();

        Assert.True(result.AllOnline);
        Assert.Equal(0, result.OnlineCount);
        Assert.Equal(0, result.OfflineCount);
    }

    [Fact]
    public async Task CheckHealthAsync_WithDrivers_WorksWithoutException()
    {
        await using var pool = CreatePool([
            new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP", Host = "127.0.0.1" }
        ]);

        await pool.GetDriverAsync("plc-1");
        var result = await pool.CheckHealthAsync();

        // 由于没有真实设备连接，预期为离线
        Assert.Equal(1, result.OfflineCount);
    }

    // ── IDeviceConnectionPool 接口 ──────────────────────────

    [Fact]
    public async Task Pool_DisposeAsync_CompletesWithoutException()
    {
        var pool = CreatePool([
            new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP", Host = "127.0.0.1" }
        ]);

        await pool.GetDriverAsync("plc-1");

        // 释放不应抛出异常
        await pool.DisposeAsync();
    }

    [Fact]
    public async Task Pool_DisposeAsync_MultipleTimes_DoesNotThrow()
    {
        var pool = CreatePool();
        await pool.DisposeAsync();
        await pool.DisposeAsync(); // 二次释放
    }

    // ── AppConfig 集成 ─────────────────────────────────────

    [Fact]
    public void AppConfig_DefaultDevices_IsEmpty()
    {
        var config = new AppConfig();
        Assert.NotNull(config.Devices);
        Assert.Empty(config.Devices);
    }

    [Fact]
    public void AppConfig_Devices_CanBeConfigured()
    {
        var config = new AppConfig
        {
            Devices =
            [
                new DeviceConfigEntry { DeviceId = "plc-1", DriverType = "ModbusTCP" },
                new DeviceConfigEntry { DeviceId = "mqtt-1", DriverType = "MQTT" }
            ]
        };

        Assert.Equal(2, config.Devices.Count);
    }

    [Fact]
    public void AppConfig_ConnectionPool_HasDefaultConfig()
    {
        var config = new AppConfig();
        Assert.NotNull(config.ConnectionPool);
        Assert.Equal(10, config.ConnectionPool.MaxConcurrentConnections);
    }

    // ── DeviceConfigEntry ModbusRTU ──────────────────────────

    [Fact]
    public void DeviceConfigEntry_ModbusRtuDefaults_AreApplied()
    {
        var entry = new DeviceConfigEntry
        {
            DeviceId = "rtu-1",
            DriverType = "ModbusRTU",
            ComPort = "COM1"
        };

        Assert.Equal("COM1", entry.ComPort);
        Assert.Equal(19200, entry.BaudRate);
        Assert.Equal(8, entry.DataBits);
        Assert.Equal("Even", entry.Parity);
        Assert.Equal("One", entry.StopBits);
    }
}
