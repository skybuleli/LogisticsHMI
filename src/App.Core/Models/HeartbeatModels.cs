namespace App.Core;

/// <summary>
/// 心跳配置。
/// </summary>
public class HeartbeatConfig
{
    public const string SectionName = "Heartbeat";

    /// <summary>心跳检查间隔（毫秒）。默认 10s。</summary>
    public int IntervalMs { get; set; } = 10_000;

    /// <summary>连续丢失 N 次心跳后触发 Missed 事件。默认 3。</summary>
    public int MissedThreshold { get; set; } = 3;

    /// <summary>单次心跳检查超时（毫秒）。默认 5s。</summary>
    public int CheckTimeoutMs { get; set; } = 5000;

    /// <summary>启动时自动开始心跳检查。</summary>
    public bool AutoStart { get; set; } = true;
}

/// <summary>
/// 单台设备的心跳状态。
/// </summary>
public sealed record HeartbeatDeviceStatus
{
    /// <summary>设备 ID。</summary>
    public required string DeviceId { get; init; }

    /// <summary>驱动名称。</summary>
    public required string DriverName { get; init; }

    /// <summary>当前连接状态。</summary>
    public required ConnectionState State { get; init; }

    /// <summary>设备是否存活（连接正常 + 心跳未超限丢失）。</summary>
    public bool IsAlive { get; init; }

    /// <summary>连续丢失心跳次数。</summary>
    public int ConsecutiveMissedCount { get; init; }

    /// <summary>上次心跳往返延迟。</summary>
    public TimeSpan? LastHeartbeatLatency { get; init; }

    /// <summary>上次成功心跳时间（UTC）。</summary>
    public DateTime? LastHeartbeatTime { get; init; }

    /// <summary>上次丢失心跳时间（UTC）。</summary>
    public DateTime? LastMissedTime { get; init; }

    /// <summary>驱动类型（ModbusTCP / ModbusRTU / S7 / OPCUA / MQTT）。</summary>
    public string? DriverType { get; init; }
}

/// <summary>
/// 心跳 Tick 事件参数（全局，包含所有设备快照）。
/// </summary>
public sealed record HeartbeatTickEventArgs
{
    /// <summary>检查时间（UTC）。</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>所有设备心跳状态快照。</summary>
    public required IReadOnlyList<HeartbeatDeviceStatus> DeviceStatuses { get; init; }

    /// <summary>在线设备数。</summary>
    public int OnlineCount { get; init; }

    /// <summary>离线/异常设备数。</summary>
    public int OfflineCount { get; init; }
}

/// <summary>
/// 单设备心跳事件参数（Missed / Recovered）。
/// </summary>
public sealed record HeartbeatDeviceEventArgs
{
    /// <summary>设备 ID。</summary>
    public required string DeviceId { get; init; }

    /// <summary>当前状态。</summary>
    public required HeartbeatDeviceStatus Status { get; init; }

    /// <summary>事件详情。</summary>
    public required string Detail { get; init; }

    /// <summary>事件时间（UTC）。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// 心跳快照（一次性查询所有设备状态）。
/// </summary>
public sealed record HeartbeatSnapshot
{
    /// <summary>快照时间（UTC）。</summary>
    public required DateTime Timestamp { get; init; }

    /// <summary>所有设备状态。</summary>
    public required IReadOnlyList<HeartbeatDeviceStatus> DeviceStatuses { get; init; }

    /// <summary>在线设备数。</summary>
    public int OnlineCount { get; init; }

    /// <summary>离线设备数。</summary>
    public int OfflineCount { get; init; }

    /// <summary>总设备数。</summary>
    public int TotalDevices { get; init; }

    /// <summary>本次检查耗时。</summary>
    public required TimeSpan Elapsed { get; init; }
}
