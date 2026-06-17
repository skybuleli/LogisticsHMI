using System.Collections.Concurrent;
using App.Core;
using Microsoft.Extensions.Logging;

namespace App.Infrastructure;

/// <summary>
/// 设备数据缓存实现。
/// <para>
/// 基于 ConcurrentDictionary 的内存缓存，支持：
/// <list type="bullet">
///   <item>每设备地址粒度的 TTL 过期控制</item>
///   <item>写直达策略（Set 同时更新缓存与发往设备）</item>
///   <item>惰性过期清理 + 后台定时清理</item>
///   <item>每设备最大条目限制（LRU 淘汰）</item>
///   <item>命中率统计</item>
/// </list>
/// </para>
/// 线程安全，所有操作无需外部同步。
/// </summary>
public sealed class DeviceDataCache : IDeviceDataCache, IDisposable
{
    private readonly IConfigurationService _configService;
    private readonly ILogger<DeviceDataCache> _logger;

    // 缓存存储：key = "{deviceId}:{address}", value = CacheEntry
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    // 设备→key 映射，用于快速按设备清空
    private readonly ConcurrentDictionary<string, HashSet<string>> _deviceKeys = new(StringComparer.OrdinalIgnoreCase);

    // 后台清理定时器
    private Timer? _cleanupTimer;
    private bool _disposed;

    // 统计
    private long _totalGets;
    private long _totalHits;
    private long _totalSets;
    private long _totalEvictions;

    /// <summary>缓存命中率（0.0 ~ 1.0）。</summary>
    public double HitRate
    {
        get
        {
            var gets = Interlocked.Read(ref _totalGets);
            return gets > 0 ? (double)Interlocked.Read(ref _totalHits) / gets : 0;
        }
    }

    /// <summary>当前缓存条目数。</summary>
    public int EntryCount => _cache.Count;

    public DeviceDataCache(
        IConfigurationService configService,
        ILogger<DeviceDataCache> logger)
    {
        _configService = configService ?? throw new ArgumentNullException(nameof(configService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        StartCleanupTimer();
    }

    /// <inheritdoc />
    public bool TryGet(string deviceId, string address, out byte[]? data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        Interlocked.Increment(ref _totalGets);

        var key = BuildKey(deviceId, address);

        if (_cache.TryGetValue(key, out var entry))
        {
            // 惰性过期检查
            if (entry.IsExpired)
            {
                _cache.TryRemove(key, out _);
                RemoveDeviceKey(deviceId, key);
                Interlocked.Increment(ref _totalEvictions);

                _logger.LogTrace("缓存过期 (惰性): {DeviceId}:{Address}", deviceId, address);
                data = null;
                return false;
            }

            Interlocked.Increment(ref _totalHits);
            data = entry.Data;
            return true;
        }

        data = null;
        return false;
    }

    /// <inheritdoc />
    public void Set(string deviceId, string address, byte[] data, TimeSpan? ttl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        ArgumentNullException.ThrowIfNull(data);

        Interlocked.Increment(ref _totalSets);

        var key = BuildKey(deviceId, address);
        var effectiveTtl = ttl ?? GetDefaultTtl();

        var entry = new CacheEntry(data, effectiveTtl);
        _cache[key] = entry;

        // 维护设备→key 映射
        _deviceKeys.AddOrUpdate(
            deviceId,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase) { key },
            (_, keys) =>
            {
                lock (keys)
                {
                    keys.Add(key);
                }
                return keys;
            });

        // 检查是否需要淘汰
        EnforceMaxEntries(deviceId);

        _logger.LogTrace("缓存写入: {DeviceId}:{Address} ({Length} bytes, TTL={Ttl})",
            deviceId, address, data.Length, effectiveTtl);
    }

    /// <inheritdoc />
    public bool Remove(string deviceId, string address)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        var key = BuildKey(deviceId, address);
        if (_cache.TryRemove(key, out _))
        {
            RemoveDeviceKey(deviceId, key);
            _logger.LogTrace("缓存移除: {DeviceId}:{Address}", deviceId, address);
            return true;
        }
        return false;
    }

    /// <inheritdoc />
    public void Clear(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);

        if (_deviceKeys.TryRemove(deviceId, out var keys))
        {
            lock (keys)
            {
                foreach (var key in keys)
                {
                    _cache.TryRemove(key, out _);
                }
            }
            _logger.LogInformation("缓存清空: {DeviceId} (移除 {Count} 条)", deviceId, keys.Count);
        }
    }

    /// <summary>
    /// 清空全部缓存。
    /// </summary>
    public void ClearAll()
    {
        _cache.Clear();
        _deviceKeys.Clear();
        _logger.LogInformation("全部缓存已清空");
    }

    /// <summary>
    /// 获取缓存统计信息。
    /// </summary>
    public DataCacheStats GetStats()
    {
        return new DataCacheStats
        {
            EntryCount = _cache.Count,
            DeviceCount = _deviceKeys.Count,
            TotalGets = Interlocked.Read(ref _totalGets),
            TotalHits = Interlocked.Read(ref _totalHits),
            TotalSets = Interlocked.Read(ref _totalSets),
            TotalEvictions = Interlocked.Read(ref _totalEvictions),
            HitRate = HitRate,
        };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _cleanupTimer?.Dispose();
        _cleanupTimer = null;

        _cache.Clear();
        _deviceKeys.Clear();
    }

    // ── 内部 ─────────────────────────────────────────────────

    private static string BuildKey(string deviceId, string address)
        => $"{deviceId}:{address}";

    private TimeSpan GetDefaultTtl()
    {
        var ms = _configService.Current.DeviceDataCache?.DefaultTtlMs ?? 200;
        return ms > 0 ? TimeSpan.FromMilliseconds(ms) : TimeSpan.MaxValue;
    }

    private int GetMaxEntriesPerDevice()
        => _configService.Current.DeviceDataCache?.MaxEntriesPerDevice ?? 1024;

    /// <summary>
    /// 对指定设备执行 LRU 淘汰，超过最大条目数时移除最旧条目。
    /// </summary>
    private void EnforceMaxEntries(string deviceId)
    {
        if (!_deviceKeys.TryGetValue(deviceId, out var keys))
            return;

        var max = GetMaxEntriesPerDevice();
        if (max <= 0) return;

        lock (keys)
        {
            while (keys.Count > max)
            {
                // 找到最旧条目淘汰（近似 LRU）
                string? oldestKey = null;
                DateTime oldestTime = DateTime.MaxValue;

                foreach (var k in keys)
                {
                    if (_cache.TryGetValue(k, out var entry) && entry.CreatedAt < oldestTime)
                    {
                        oldestTime = entry.CreatedAt;
                        oldestKey = k;
                    }
                }

                if (oldestKey is null) break;

                _cache.TryRemove(oldestKey, out _);
                keys.Remove(oldestKey);
                Interlocked.Increment(ref _totalEvictions);
            }
        }
    }

    private void RemoveDeviceKey(string deviceId, string key)
    {
        if (_deviceKeys.TryGetValue(deviceId, out var keys))
        {
            lock (keys)
            {
                keys.Remove(key);
                if (keys.Count == 0)
                {
                    _deviceKeys.TryRemove(deviceId, out _);
                }
            }
        }
    }

    private void StartCleanupTimer()
    {
        var interval = _configService.Current.DeviceDataCache?.CleanupIntervalMs ?? 5000;
        if (interval <= 0) return; // 仅惰性清理

        _cleanupTimer = new Timer(
            _ => CleanupExpired(),
            null,
            interval,
            interval);
    }

    /// <summary>
    /// 后台清理过期条目。
    /// </summary>
    private void CleanupExpired()
    {
        try
        {
            if (_disposed) return;

            var expiredCount = 0;
            foreach (var kvp in _cache)
            {
                if (kvp.Value.IsExpired && _cache.TryRemove(kvp.Key, out _))
                {
                    // 从设备映射中移除
                    var colonIdx = kvp.Key.IndexOf(':');
                    if (colonIdx > 0)
                    {
                        var deviceId = kvp.Key[..colonIdx];
                        RemoveDeviceKey(deviceId, kvp.Key);
                    }
                    expiredCount++;
                }
            }

            if (expiredCount > 0)
            {
                Interlocked.Add(ref _totalEvictions, expiredCount);
                _logger.LogDebug("后台清理: 移除 {Count} 条过期缓存", expiredCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "缓存清理异常");
        }
    }

    // ── 嵌套类型 ─────────────────────────────────────────────

    /// <summary>
    /// 缓存条目。
    /// </summary>
    internal sealed class CacheEntry
    {
        public byte[] Data { get; }
        public DateTime CreatedAt { get; }
        public TimeSpan Ttl { get; }

        public CacheEntry(byte[] data, TimeSpan ttl)
        {
            Data = data;
            CreatedAt = DateTime.UtcNow;
            Ttl = ttl;
        }

        public bool IsExpired => Ttl != TimeSpan.MaxValue && DateTime.UtcNow - CreatedAt >= Ttl;
    }
}

// DataCacheStats 定义已移至 App.Core/Models/DataCacheStats.cs
