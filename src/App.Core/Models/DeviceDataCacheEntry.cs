namespace App.Core;

/// <summary>
/// 内存缓存数据项。
/// </summary>
public sealed class DeviceDataCacheEntry
{
    public required string DeviceId { get; init; }
    public required string Address { get; init; }
    public required byte[] Data { get; set; }
    public required DateTimeOffset UpdatedAt { get; set; }
    public TimeSpan Ttl { get; init; }
}
