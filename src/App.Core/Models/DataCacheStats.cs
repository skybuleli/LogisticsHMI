namespace App.Core;

/// <summary>
/// 设备数据缓存统计信息。
/// </summary>
public sealed record DataCacheStats
{
    /// <summary>当前条目数。</summary>
    public int EntryCount { get; init; }

    /// <summary>缓存设备数。</summary>
    public int DeviceCount { get; init; }

    /// <summary>总读取次数。</summary>
    public long TotalGets { get; init; }

    /// <summary>总命中次数。</summary>
    public long TotalHits { get; init; }

    /// <summary>总写入次数。</summary>
    public long TotalSets { get; init; }

    /// <summary>总淘汰次数（过期 + LRU）。</summary>
    public long TotalEvictions { get; init; }

    /// <summary>命中率 (0.0 ~ 1.0)。</summary>
    public double HitRate { get; init; }

    /// <summary>获取格式化命中率。</summary>
    public string HitRateDisplay => $"{HitRate:P1}";
}
