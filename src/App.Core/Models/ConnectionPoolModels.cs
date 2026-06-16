namespace App.Core;

/// <summary>
/// 连接池事件参数。
/// </summary>
/// <param name="eventType">事件类型。</param>
/// <param name="deviceId">关联设备 ID。</param>
/// <param name="detail">事件详情。</param>
public sealed record ConnectionPoolEventArgs(
    string EventType,
    string? DeviceId,
    string Detail);

/// <summary>
/// 连接池统计信息。
/// </summary>
public sealed record ConnectionPoolStats
{
    /// <summary>当前活跃驱动数。</summary>
    public int ActiveDriverCount { get; init; }

    /// <summary>总连接成功次数。</summary>
    public long TotalConnects { get; init; }

    /// <summary>总连接失败次数。</summary>
    public long TotalConnectFailures { get; init; }

    /// <summary>总断开次数。</summary>
    public long TotalDisconnects { get; init; }

    /// <summary>当前等待中的操作数。</summary>
    public int PendingOperations { get; init; }

    /// <summary>池已启动的运行时长。</summary>
    public TimeSpan Uptime { get; init; }

    /// <summary>最后一次健康检查结果。</summary>
    public HealthCheckResult? LastHealthCheck { get; init; }
}

/// <summary>
/// 健康检查结果。
/// </summary>
public sealed record HealthCheckResult
{
    /// <summary>检查时间。</summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>在线设备数。</summary>
    public int OnlineCount { get; init; }

    /// <summary>离线设备数。</summary>
    public int OfflineCount { get; init; }

    /// <summary>异常设备数。</summary>
    public int ErrorCount { get; init; }

    /// <summary>健康检查耗时。</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>离线设备 ID 列表。</summary>
    public IReadOnlyList<string> OfflineDeviceIds { get; init; } = [];

    /// <summary>异常设备 ID 列表。</summary>
    public IReadOnlyList<string> ErrorDeviceIds { get; init; } = [];

    /// <summary>是否所有设备在线。</summary>
    public bool AllOnline => OfflineCount == 0 && ErrorCount == 0;
}
