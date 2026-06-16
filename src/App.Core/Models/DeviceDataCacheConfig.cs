namespace App.Core;

/// <summary>
/// 设备数据缓存配置。
/// </summary>
public class DeviceDataCacheConfig
{
    public const string SectionName = "DeviceDataCache";

    /// <summary>默认缓存 TTL（毫秒）。0 表示永不过期。默认 200ms（与设备扫描间隔匹配）。</summary>
    public int DefaultTtlMs { get; set; } = 200;

    /// <summary>每台设备最大缓存条目数。超限时淘汰最旧条目。默认 1024。</summary>
    public int MaxEntriesPerDevice { get; set; } = 1024;

    /// <summary>缓存清理间隔（毫秒）。后台定时清理过期条目。0 表示仅惰性清理。默认 5000。</summary>
    public int CleanupIntervalMs { get; set; } = 5000;

    /// <summary>是否启用统计（计数/命中率）。默认启用。</summary>
    public bool EnableStats { get; set; } = true;
}
