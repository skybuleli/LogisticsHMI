namespace App.Core;

/// <summary>
/// 连接池全局配置。
/// </summary>
public class ConnectionPoolConfig
{
    public const string SectionName = "ConnectionPool";

    /// <summary>全局最大并发连接操作数。</summary>
    public int MaxConcurrentConnections { get; set; } = 10;

    /// <summary>健康检查间隔（毫秒），0 表示不启用。</summary>
    public int HealthCheckIntervalMs { get; set; } = 30000;

    /// <summary>启动时自动开始健康检查。</summary>
    public bool AutoStartHealthCheck { get; set; } = true;

    /// <summary>单次健康检查超时（毫秒）。</summary>
    public int HealthCheckTimeoutMs { get; set; } = 5000;
}
