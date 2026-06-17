namespace App.Core;

/// <summary>
/// 设备配置条目（JSON deserialization target）。
/// <para>
/// 包含所有协议驱动的公共 + 专用属性。DriverType 决定哪些属性有效。
/// </para>
/// </summary>
public class DeviceConfigEntry
{
    // ── 唯一标识 ────────────────────────────────────────────
    /// <summary>设备唯一标识。</summary>
    public required string DeviceId { get; init; }

    /// <summary>驱动类型：ModbusTCP / ModbusRTU / S7 / OPCUA / MQTT / VirtualDevice。</summary>
    public string DriverType { get; set; } = "ModbusTCP";

    /// <summary>显示名称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    // ── 公共连接参数 ────────────────────────────────────────
    /// <summary>连接超时（毫秒）。</summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>心跳间隔（毫秒）。</summary>
    public int HeartbeatIntervalMs { get; set; } = 30000;

    /// <summary>是否启用自动重连。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>重连最多尝试次数。</summary>
    public int ReconnectMaxRetries { get; set; } = 5;

    /// <summary>重连指数退避基础延迟（毫秒）。</summary>
    public int ReconnectBaseDelayMs { get; set; } = 1000;

    // ── Modbus TCP / RTU ────────────────────────────────────
    /// <summary>主机地址。</summary>
    public string? Host { get; set; }

    /// <summary>端口。0 表示使用协议默认端口（Modbus 502, MQTT 1883, OPC UA 4840）。</summary>
    public int Port { get; set; } = 0;

    /// <summary>Modbus 从机地址（1~247）。</summary>
    public byte SlaveAddress { get; set; } = 1;

    /// <summary>TCP 连接后替换单元标识符。</summary>
    public bool UseUnitId { get; set; } = true;

    /// <summary>串口名（Modbus RTU）。</summary>
    public string? ComPort { get; set; }

    /// <summary>波特率（Modbus RTU）。</summary>
    public int BaudRate { get; set; } = 19200;

    /// <summary>数据位（Modbus RTU）。</summary>
    public int DataBits { get; set; } = 8;

    /// <summary>校验位：None/Odd/Even（Modbus RTU）。</summary>
    public string Parity { get; set; } = "Even";

    /// <summary>停止位：1/1.5/2（Modbus RTU）。</summary>
    public string StopBits { get; set; } = "One";

    // ── Siemens S7 ──────────────────────────────────────────
    /// <summary>PLC IP 地址。</summary>
    public string? IpAddress { get; set; }

    /// <summary>CPU 类型：S71200 / S71500 / S7300 / S7400 / S7200。</summary>
    public string CpuType { get; set; } = "S71200";

    /// <summary>Rack 号。</summary>
    public int Rack { get; set; }

    /// <summary>Slot 号。</summary>
    public int Slot { get; set; } = 2;

    /// <summary>TSAP 源。</summary>
    public ushort? LocalTsap { get; set; }

    /// <summary>TSAP 目标。</summary>
    public ushort? RemoteTsap { get; set; }

    // ── MQTT ────────────────────────────────────────────────
    /// <summary>Broker 地址（MQTT）。</summary>
    public string? Broker { get; set; }

    /// <summary>用户名。</summary>
    public string? UserName { get; set; }

    /// <summary>密码。</summary>
    public string? Password { get; set; }

    /// <summary>客户端 ID。</summary>
    public string? ClientId { get; set; }

    /// <summary>Will Topic。</summary>
    public string? WillTopic { get; set; }

    /// <summary>是否启用 TLS。</summary>
    public bool UseTls { get; set; }

    /// <summary>QoS 等级（0/1/2）。</summary>
    public int Qos { get; set; } = 1;

    // ── OPC UA ──────────────────────────────────────────────
    /// <summary>OPC UA 服务器 URL。</summary>
    public string? ServerUrl { get; set; }

    /// <summary>安全策略：None/Sign/Encrypt。</summary>
    public string SecurityPolicy { get; set; } = "None";

    /// <summary>会话名称。</summary>
    public string? SessionName { get; set; }

    // ── Virtual Device ─────────────────────────────────────
    /// <summary>虚拟设备类型标签，如 Conveyor / Stacker / Sensor。</summary>
    public string DeviceProfile { get; set; } = "Generic";
}
