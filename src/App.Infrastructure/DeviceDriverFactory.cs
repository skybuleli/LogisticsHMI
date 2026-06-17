using App.Core;
using App.Infrastructure.Drivers;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure;

/// <summary>
/// 设备驱动工厂实现。
/// <para>
/// 根据 <see cref="DeviceConfigEntry.DriverType"/> 区分器创建对应的驱动实例。
/// </para>
/// </summary>
public sealed class DeviceDriverFactory : IDeviceDriverFactory
{
    private readonly ILoggerFactory _loggerFactory;

    public DeviceDriverFactory(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
    }

    /// <inheritdoc />
    public IDeviceDriver Create(DeviceConfigEntry config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.DriverType.ToUpperInvariant() switch
        {
            "MODBUSTCP" => CreateModbusTcp(config),
            "MODBUSRTU" => CreateModbusRtu(config),
            "S7" => CreateS7(config),
            "OPCUA" => CreateOpcUa(config),
            "MQTT" => CreateMqtt(config),
            "VIRTUALDEVICE" => CreateVirtualDevice(config),
            _ => throw new ArgumentException(
                $"不支持的驱动类型: '{config.DriverType}'。支持的类型: ModbusTCP, ModbusRTU, S7, OPCUA, MQTT, VirtualDevice", nameof(config))
        };
    }

    private static SerialParity ParseParity(string? parity) => (parity?.ToLowerInvariant()) switch
    {
        "none" => SerialParity.None,
        "odd" => SerialParity.Odd,
        "even" => SerialParity.Even,
        "mark" => SerialParity.Mark,
        "space" => SerialParity.Space,
        _ => SerialParity.Even  // 默认偶校验（Modbus RTU 常用）
    };

    private static SerialStopBits ParseStopBits(string? stopBits) => (stopBits?.ToLowerInvariant()) switch
    {
        "none" or "0" => SerialStopBits.None,
        "one" or "1" => SerialStopBits.One,
        "onepointfive" or "1.5" => SerialStopBits.OnePointFive,
        "two" or "2" => SerialStopBits.Two,
        _ => SerialStopBits.One
    };

    private ModbusTcpDriver CreateModbusTcp(DeviceConfigEntry config)
    {
        var driverCfg = new ModbusDriverConfig
        {
            DeviceId = config.DeviceId,
            DisplayName = config.DisplayName,
            TimeoutMs = config.TimeoutMs,
            HeartbeatIntervalMs = config.HeartbeatIntervalMs,
            AutoReconnect = config.AutoReconnect,
            ReconnectMaxRetries = config.ReconnectMaxRetries,
            ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
            Host = config.Host ?? "127.0.0.1",
            Port = config.Port > 0 ? config.Port : 502,
            SlaveAddress = config.SlaveAddress,
            UseUnitId = config.UseUnitId
        };

        var logger = _loggerFactory.CreateLogger<ModbusTcpDriver>();
        return new ModbusTcpDriver(driverCfg, logger);
    }

    private ModbusRtuDriver CreateModbusRtu(DeviceConfigEntry config)
    {
        var driverCfg = new ModbusRtuDriverConfig
        {
            DeviceId = config.DeviceId,
            DisplayName = config.DisplayName,
            TimeoutMs = config.TimeoutMs,
            HeartbeatIntervalMs = config.HeartbeatIntervalMs,
            AutoReconnect = config.AutoReconnect,
            ReconnectMaxRetries = config.ReconnectMaxRetries,
            ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
            PortName = config.ComPort ?? "COM1",
            BaudRate = config.BaudRate,
            DataBits = config.DataBits,
            Parity = ParseParity(config.Parity),
            StopBits = ParseStopBits(config.StopBits),
            SlaveAddress = config.SlaveAddress
        };

        var logger = _loggerFactory.CreateLogger<ModbusRtuDriver>();
        return new ModbusRtuDriver(driverCfg, logger);
    }

    private S7Driver CreateS7(DeviceConfigEntry config)
    {
        var driverCfg = new S7DriverConfig
        {
            DeviceId = config.DeviceId,
            DisplayName = config.DisplayName,
            TimeoutMs = config.TimeoutMs,
            HeartbeatIntervalMs = config.HeartbeatIntervalMs,
            AutoReconnect = config.AutoReconnect,
            ReconnectMaxRetries = config.ReconnectMaxRetries,
            ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
            IpAddress = config.IpAddress ?? "192.168.0.1",
            CpuType = config.CpuType,
            Rack = config.Rack,
            Slot = config.Slot,
            LocalTsap = config.LocalTsap,
            RemoteTsap = config.RemoteTsap
        };

        var logger = _loggerFactory.CreateLogger<S7Driver>();
        return new S7Driver(driverCfg, logger);
    }

    private OpcUaDriver CreateOpcUa(DeviceConfigEntry config)
    {
        var driverCfg = new OpcUaDriverConfig
        {
            DeviceId = config.DeviceId,
            DisplayName = config.DisplayName,
            TimeoutMs = config.TimeoutMs,
            HeartbeatIntervalMs = config.HeartbeatIntervalMs,
            AutoReconnect = config.AutoReconnect,
            ReconnectMaxRetries = config.ReconnectMaxRetries,
            ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
            ServerUrl = config.ServerUrl ?? "opc.tcp://127.0.0.1:4840",
            UserName = config.UserName,
            Password = config.Password,
            SecurityPolicy = config.SecurityPolicy,
            SessionName = config.SessionName ?? $"LogisticsHMI/{config.DeviceId}"
        };

        var logger = _loggerFactory.CreateLogger<OpcUaDriver>();
        return new OpcUaDriver(driverCfg, logger);
    }

    private MqttDriver CreateMqtt(DeviceConfigEntry config)
    {
        var driverCfg = new MqttDriverConfig
        {
            DeviceId = config.DeviceId,
            DisplayName = config.DisplayName,
            TimeoutMs = config.TimeoutMs,
            HeartbeatIntervalMs = config.HeartbeatIntervalMs,
            AutoReconnect = config.AutoReconnect,
            ReconnectMaxRetries = config.ReconnectMaxRetries,
            ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
            Broker = config.Broker ?? "127.0.0.1",
            Port = config.Port > 0 ? config.Port : 1883,
            UserName = config.UserName,
            Password = config.Password,
            ClientId = config.ClientId ?? $"LogisticsHMI_{Guid.NewGuid():N}",
            WillTopic = config.WillTopic,
            UseTls = config.UseTls,
            Qos = config.Qos
        };

        var logger = _loggerFactory.CreateLogger<MqttDriver>();
        return new MqttDriver(driverCfg, logger);
    }

    private VirtualDeviceDriver CreateVirtualDevice(DeviceConfigEntry config)
    {
        var driverCfg = new VirtualDeviceDriverConfig
        {
            DeviceId = config.DeviceId,
            DisplayName = config.DisplayName,
            TimeoutMs = config.TimeoutMs,
            HeartbeatIntervalMs = config.HeartbeatIntervalMs,
            AutoReconnect = config.AutoReconnect,
            ReconnectMaxRetries = config.ReconnectMaxRetries,
            ReconnectBaseDelayMs = config.ReconnectBaseDelayMs,
            DeviceProfile = config.DeviceProfile
        };

        var logger = _loggerFactory.CreateLogger<VirtualDeviceDriver>();
        return new VirtualDeviceDriver(driverCfg, logger);
    }
}
