namespace App.Core;

/// <summary>
/// 驱动配置基类。
/// </summary>
public abstract class DriverConfigBase
{
    /// <summary>
    /// 设备唯一标识。
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// 显示名称。
    /// </summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// 连接超时（毫秒）。
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    /// 心跳间隔（毫秒）。
    /// </summary>
    public int HeartbeatIntervalMs { get; set; } = 30000;

    /// <summary>
    /// 是否启用自动重连。
    /// </summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>
    /// 重连最多尝试次数。
    /// </summary>
    public int ReconnectMaxRetries { get; set; } = 5;

    /// <summary>
    /// 重连指数退避的基础延迟（毫秒）。
    /// 第 N 次重试等待时间 = Min(ReconnectBaseDelayMs × 2^(N-1), 30s)。
    /// </summary>
    public int ReconnectBaseDelayMs { get; set; } = 1000;
}
