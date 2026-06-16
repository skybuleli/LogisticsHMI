using App.Core;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace App.Infrastructure.Tests;

/// <summary>
/// <see cref="DeviceDataCache"/> 单元测试。
/// 覆盖 CRUD、TTL 过期、LRU 淘汰、统计、并发。
/// </summary>
public class DeviceDataCacheTests
{
    private readonly ITestOutputHelper _output;

    public DeviceDataCacheTests(ITestOutputHelper output)
    {
        _output = output;
    }

    // ═══════════════════════════════════════════════════════════
    // 配置默认值
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DeviceDataCacheConfig_DefaultValues()
    {
        var config = new DeviceDataCacheConfig();
        Assert.Equal(200, config.DefaultTtlMs);
        Assert.Equal(1024, config.MaxEntriesPerDevice);
        Assert.Equal(5000, config.CleanupIntervalMs);
        Assert.True(config.EnableStats);
    }

    [Fact]
    public void DeviceDataCacheConfig_CustomValues_AreApplied()
    {
        var config = new DeviceDataCacheConfig
        {
            DefaultTtlMs = 1000,
            MaxEntriesPerDevice = 64,
            CleanupIntervalMs = 10000,
            EnableStats = false
        };

        Assert.Equal(1000, config.DefaultTtlMs);
        Assert.Equal(64, config.MaxEntriesPerDevice);
        Assert.Equal(10000, config.CleanupIntervalMs);
        Assert.False(config.EnableStats);
    }

    // ═══════════════════════════════════════════════════════════
    // 基础 CRUD
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Set_Then_TryGet_ReturnsData()
    {
        var cache = CreateCache();

        cache.Set("device-1", "03:100", new byte[] { 0x01, 0x02 });

        var found = cache.TryGet("device-1", "03:100", out var data);
        Assert.True(found);
        Assert.NotNull(data);
        Assert.Equal([0x01, 0x02], data);
    }

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        var cache = CreateCache();

        var found = cache.TryGet("nonexistent", "03:100", out var data);

        Assert.False(found);
        Assert.Null(data);
    }

    [Fact]
    public void TryGet_DifferentDevice_Isolated()
    {
        var cache = CreateCache();

        cache.Set("device-a", "03:100", new byte[] { 0xAA });
        cache.Set("device-b", "03:100", new byte[] { 0xBB });

        cache.TryGet("device-a", "03:100", out var dataA);
        cache.TryGet("device-b", "03:100", out var dataB);

        Assert.Equal([0xAA], dataA);
        Assert.Equal([0xBB], dataB);
    }

    [Fact]
    public void Set_Overwrite_UpdatesValue()
    {
        var cache = CreateCache();

        cache.Set("dev", "addr", new byte[] { 0x01 });
        cache.Set("dev", "addr", new byte[] { 0xFF });

        cache.TryGet("dev", "addr", out var data);
        Assert.Equal([0xFF], data);
    }

    [Fact]
    public void Remove_Existing_ReturnsTrue()
    {
        var cache = CreateCache();

        cache.Set("dev", "addr", new byte[] { 0x01 });
        var removed = cache.Remove("dev", "addr");

        Assert.True(removed);
        Assert.False(cache.TryGet("dev", "addr", out _));
    }

    [Fact]
    public void Remove_Missing_ReturnsFalse()
    {
        var cache = CreateCache();

        var removed = cache.Remove("ghost", "addr");

        Assert.False(removed);
    }

    [Fact]
    public void Clear_DeviceOnly_RemovesThatDeviceData()
    {
        var cache = CreateCache();

        cache.Set("dev-a", "addr1", new byte[] { 0x01 });
        cache.Set("dev-a", "addr2", new byte[] { 0x02 });
        cache.Set("dev-b", "addr1", new byte[] { 0xFF });

        cache.Clear("dev-a");

        Assert.False(cache.TryGet("dev-a", "addr1", out _));
        Assert.False(cache.TryGet("dev-a", "addr2", out _));
        Assert.True(cache.TryGet("dev-b", "addr1", out _)); // dev-b 还在
    }

    [Fact]
    public void Clear_NonexistentDevice_NoError()
    {
        var cache = CreateCache();

        cache.Clear("ghost-device"); // 不应抛异常
    }

    // ═══════════════════════════════════════════════════════════
    // TTL 过期
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryGet_AfterTtlExpired_ReturnsFalse()
    {
        var cache = CreateCache(ttlMs: 50); // 50ms TTL

        cache.Set("dev", "addr", new byte[] { 0x01 });

        // 立即读取应该命中
        Assert.True(cache.TryGet("dev", "addr", out _));

        // 等 TTL 过
        Thread.Sleep(60);

        Assert.False(cache.TryGet("dev", "addr", out _));
    }

    [Fact]
    public void Set_WithCustomTtl_OverridesDefault()
    {
        var cache = CreateCache(ttlMs: 10_000); // 默认 10s

        // 自定义 30ms 短 TTL
        cache.Set("dev", "short", new byte[] { 0x01 }, TimeSpan.FromMilliseconds(30));
        // 默认 TTL
        cache.Set("dev", "long", new byte[] { 0xFF });

        Thread.Sleep(40);

        // 短 TTL 应该过期
        Assert.False(cache.TryGet("dev", "short", out _));
        // 长 TTL 应该还在
        Assert.True(cache.TryGet("dev", "long", out _));
    }

    [Fact]
    public void Set_WithZeroTtl_NeverExpires()
    {
        var cache = CreateCache(ttlMs: 0);

        cache.Set("dev", "perm", new byte[] { 0x42 });

        Thread.Sleep(50);

        Assert.True(cache.TryGet("dev", "perm", out var data));
        Assert.Equal([0x42], data);
    }

    // ═══════════════════════════════════════════════════════════
    // LRU 淘汰
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Set_WhenExceedsMaxEntries_EvictsOldest()
    {
        var cache = CreateCache(ttlMs: 0, maxEntries: 3);

        cache.Set("dev", "addr1", new byte[] { 0x01 });
        cache.Set("dev", "addr2", new byte[] { 0x02 });
        cache.Set("dev", "addr3", new byte[] { 0x03 });

        // 现在插入第 4 条，应淘汰 addr1（最旧）
        cache.Set("dev", "addr4", new byte[] { 0x04 });

        Assert.False(cache.TryGet("dev", "addr1", out _), "addr1 应被淘汰");
        Assert.True(cache.TryGet("dev", "addr2", out _), "addr2 应存在");
        Assert.True(cache.TryGet("dev", "addr3", out _), "addr3 应存在");
        Assert.True(cache.TryGet("dev", "addr4", out _), "addr4 应存在");
    }

    [Fact]
    public void Set_Eviction_OnlyAffectsTargetDevice()
    {
        var cache = CreateCache(ttlMs: 0, maxEntries: 2);

        cache.Set("dev-limited", "addr1", new byte[] { 0x01 });
        cache.Set("dev-unlimited", "addr1", new byte[] { 0xFF });
        cache.Set("dev-unlimited", "addr2", new byte[] { 0xFE });
        cache.Set("dev-unlimited", "addr3", new byte[] { 0xFD });

        // dev-limited 只有 1 条，不应被淘汰
        Assert.True(cache.TryGet("dev-limited", "addr1", out _));
    }

    // ═══════════════════════════════════════════════════════════
    // 参数校验
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void TryGet_NullDeviceId_Throws()
    {
        var cache = CreateCache();
        Assert.Throws<ArgumentNullException>(() => cache.TryGet(null!, "addr", out _));
    }

    [Fact]
    public void TryGet_EmptyAddress_Throws()
    {
        var cache = CreateCache();
        Assert.Throws<ArgumentException>(() => cache.TryGet("dev", "", out _));
    }

    [Fact]
    public void Set_NullData_Throws()
    {
        var cache = CreateCache();
        Assert.Throws<ArgumentNullException>(() => cache.Set("dev", "addr", null!));
    }

    // ═══════════════════════════════════════════════════════════
    // 统计
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GetStats_HitRate_TracksCorrectly()
    {
        var cache = CreateCache(ttlMs: 0);

        cache.Set("dev", "addr", new byte[] { 0x01 });

        // 3 次命中
        cache.TryGet("dev", "addr", out _);
        cache.TryGet("dev", "addr", out _);
        cache.TryGet("dev", "addr", out _);

        // 1 次未命中
        cache.TryGet("dev", "missing", out _);

        var stats = cache.GetStats();
        Assert.Equal(4, stats.TotalGets);
        Assert.Equal(3, stats.TotalHits);
        Assert.Equal(1, stats.TotalSets);
        Assert.Equal(0.75, stats.HitRate, 2);
        Assert.Equal("75.0%", stats.HitRateDisplay);
    }

    [Fact]
    public void GetStats_EmptyCache_ReturnsZero()
    {
        var cache = CreateCache();

        var stats = cache.GetStats();

        Assert.Equal(0, stats.EntryCount);
        Assert.Equal(0, stats.DeviceCount);
        Assert.Equal(0, stats.TotalGets);
        Assert.Equal(0, stats.HitRate);
    }

    // ═══════════════════════════════════════════════════════════
    // 并发安全
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ConcurrentAccess_NoCorruption()
    {
        var cache = CreateCache(ttlMs: 0);

        var tasks = new List<Task>();
        for (int i = 0; i < 10; i++)
        {
            var deviceId = $"dev-{i}";
            tasks.Add(Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                {
                    cache.Set(deviceId, $"addr-{j}", new byte[] { (byte)j });
                    cache.TryGet(deviceId, $"addr-{j}", out _);
                }
            }));
        }

        Task.WaitAll([.. tasks]);

        var stats = cache.GetStats();
        Assert.Equal(10 * 100, stats.TotalSets);
        Assert.True(stats.EntryCount > 0);
    }

    // ═══════════════════════════════════════════════════════════
    // Cleanup 定时器验证
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Dispose_StopsCleanup_NoError()
    {
        var cache = CreateCache();
        cache.Dispose(); // 不应抛异常
        cache.Dispose(); // 二次释放也不应抛
    }

    // ═══════════════════════════════════════════════════════════
    // 帮助方法
    // ═══════════════════════════════════════════════════════════

    private DeviceDataCache CreateCache(int ttlMs = 10_000, int maxEntries = 1024)
    {
        var loggerFactory = LoggerFactory.Create(b => { });
        var configService = new CacheMockConfigService(ttlMs, maxEntries);
        var logger = loggerFactory.CreateLogger<DeviceDataCache>();

        return new DeviceDataCache(configService, logger);
    }

    private sealed class CacheMockConfigService : IConfigurationService
    {
        public CacheMockConfigService(int ttlMs, int maxEntries)
        {
            Current = new AppConfig
            {
                DeviceDataCache = new DeviceDataCacheConfig
                {
                    DefaultTtlMs = ttlMs,
                    MaxEntriesPerDevice = maxEntries,
                    CleanupIntervalMs = 0, // 禁用后台清理（测试中手动控制）
                    EnableStats = true
                }
            };
        }

        public AppConfig Current { get; set; }
        public LoggingConfig Logging => Current.Logging;
        public WindowConfig Window => Current.Window;
        public ThemeConfig Theme => Current.Theme;
        public CommunicationConfig Communication => Current.Communication;
        public DeviceScanConfig DeviceScan => Current.DeviceScan;
        public HeartbeatConfig Heartbeat => Current.Heartbeat;
        public DeviceDataCacheConfig DeviceDataCache => Current.DeviceDataCache;

        public event EventHandler<ConfigChangedEventArgs>? ConfigChanged
        {
            add { }
            remove { }
        }

        public void Reload() { }
        public void Save() { }
    }
}
