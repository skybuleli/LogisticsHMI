using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using App.Core;

namespace App.UI.ViewModels;

/// <summary>
/// 实时监控页面 ViewModel。
/// 提供连接池状态、设备列表、缓存统计、地址调试工具。
/// </summary>
public partial class MonitorViewModel : ViewModelBase, IDisposable
{
    private readonly IDeviceConnectionPool _pool;
    private readonly IDeviceDataCache _cache;
    private readonly IHeartbeatService _heartbeat;
    private readonly IConfigurationService _configService;
    private readonly Timer _refreshTimer;

    public MonitorViewModel(
        IDeviceConnectionPool pool,
        IDeviceDataCache cache,
        IHeartbeatService heartbeat,
        IConfigurationService configService)
    {
        _pool = pool;
        _cache = cache;
        _heartbeat = heartbeat;
        _configService = configService;
        Title = "通信诊断";

        // 每 2 秒自动刷新
        _refreshTimer = new Timer(_ => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshData), null,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));

        // 初始加载
        RefreshData();
    }

    // ═══════════════════════════════════════════════════════════
    // 连接池概览
    // ═══════════════════════════════════════════════════════════

    [ObservableProperty]
    private int _totalDevices;

    [ObservableProperty]
    private int _onlineDevices;

    [ObservableProperty]
    private int _offlineDevices;

    [ObservableProperty]
    private long _totalConnects;

    [ObservableProperty]
    private long _totalConnectFailures;

    [ObservableProperty]
    private string _poolUptime = "—";

    // ═══════════════════════════════════════════════════════════
    // 设备列表
    // ═══════════════════════════════════════════════════════════

    public ObservableCollection<DeviceItemViewModel> DeviceItems { get; } = [];

    // ═══════════════════════════════════════════════════════════
    // 缓存统计
    // ═══════════════════════════════════════════════════════════

    [ObservableProperty]
    private int _cacheEntryCount;

    [ObservableProperty]
    private string _cacheHitRate = "—";

    [ObservableProperty]
    private long _cacheTotalGets;

    [ObservableProperty]
    private long _cacheTotalSets;

    [ObservableProperty]
    private long _cacheTotalEvictions;

    // ═══════════════════════════════════════════════════════════
    // 地址调试工具
    // ═══════════════════════════════════════════════════════════

    [ObservableProperty]
    private string _debugDeviceId = string.Empty;

    [ObservableProperty]
    private string _debugAddress = string.Empty;

    [ObservableProperty]
    private int _debugLength = 4;

    [ObservableProperty]
    private string _debugResult = string.Empty;

    [ObservableProperty]
    private bool _isDebugBusy;

    // ═══════════════════════════════════════════════════════════
    // 心跳概览
    // ═══════════════════════════════════════════════════════════

    [ObservableProperty]
    private bool _heartbeatRunning;

    [ObservableProperty]
    private string _lastRefreshTime = string.Empty;

    // ═══════════════════════════════════════════════════════════
    // 命令
    // ═══════════════════════════════════════════════════════════

    [RelayCommand]
    private void Refresh()
    {
        RefreshData();
    }

    [RelayCommand]
    private async Task ReadAddress()
    {
        if (string.IsNullOrWhiteSpace(DebugDeviceId) || string.IsNullOrWhiteSpace(DebugAddress))
        {
            DebugResult = "⚠ 请填写设备 ID 和地址";
            return;
        }

        IsDebugBusy = true;
        DebugResult = string.Empty;

        try
        {
            var hit = _cache.TryGet(DebugDeviceId.Trim(), DebugAddress.Trim(), out var cachedData);
            if (hit && cachedData is not null)
            {
                DebugResult = $"✓ 缓存命中  ({cachedData.Length} bytes)\nHex: {BitConverter.ToString(cachedData)}";
            }
            else
            {
                // 尝试通过连接池读取
                var driverResult = await _pool.GetDriverAsync(DebugDeviceId.Trim());
                if (driverResult.IsSuccess && driverResult.Value is { } driver)
                {
                    var read = await driver.ReadAsync(DebugAddress.Trim(), (ushort)DebugLength);
                    if (read.IsSuccess && read.Value is not null)
                    {
                        DebugResult = $"✓ 直读成功  ({read.Value.Length} bytes)\nHex: {BitConverter.ToString(read.Value)}";
                    }
                    else
                    {
                        DebugResult = $"✗ 读取失败: {read.Error}";
                    }
                }
                else
                {
                    DebugResult = $"✗ 缓存未命中，且设备 '{DebugDeviceId}' 不在连接池中";
                }
            }
        }
        catch (Exception ex)
        {
            DebugResult = $"✗ 异常: {ex.Message}";
        }
        finally
        {
            IsDebugBusy = false;
        }
    }

    [RelayCommand]
    private void ClearDeviceCache(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            _cache.ClearAll();
            DebugResult = "✓ 全部缓存已清空";
        }
        else
        {
            _cache.Clear(deviceId);
            DebugResult = $"✓ 设备 '{deviceId}' 缓存已清空";
        }

        RefreshData();
    }

    // ═══════════════════════════════════════════════════════════
    // 内部
    // ═══════════════════════════════════════════════════════════

    private void RefreshData()
    {
        try
        {
            // ── 连接池状态 ──
            var drivers = _pool.GetAllDrivers();
            TotalDevices = drivers.Count;
            OnlineDevices = drivers.Count(d => d.State == ConnectionState.Connected);
            OfflineDevices = drivers.Count(d => d.State != ConnectionState.Connected);

            var poolStats = _pool.GetStats();
            TotalConnects = poolStats.TotalConnects;
            TotalConnectFailures = poolStats.TotalConnectFailures;
            PoolUptime = poolStats.Uptime.TotalHours >= 1
                ? $"{(int)poolStats.Uptime.TotalHours}h {poolStats.Uptime.Minutes}m"
                : $"{poolStats.Uptime.Minutes}m {poolStats.Uptime.Seconds}s";

            // ── 设备列表 ──
            DeviceItems.Clear();
            foreach (var driver in drivers)
            {
                DeviceItems.Add(new DeviceItemViewModel
                {
                    DeviceId = ExtractDeviceId(driver.DriverName),
                    DriverName = driver.DriverName,
                    DriverType = GetDriverType(driver.DriverName),
                    State = driver.State,
                    IsOnline = driver.State == ConnectionState.Connected
                });
            }

            // 如果没有实际连接驱动，显示配置中的设备
            if (drivers.Count == 0)
            {
                foreach (var cfg in _configService.Current.Devices)
                {
                    DeviceItems.Add(new DeviceItemViewModel
                    {
                        DeviceId = cfg.DeviceId,
                        DriverName = $"{cfg.DriverType}-{cfg.DeviceId}",
                        DriverType = cfg.DriverType,
                        State = ConnectionState.Disconnected,
                        IsOnline = false
                    });
                }
            }

            // ── 缓存统计 ──
            var cacheStats = _cache.GetStats();
            CacheEntryCount = cacheStats.EntryCount;
            CacheHitRate = cacheStats.HitRateDisplay;
            CacheTotalGets = cacheStats.TotalGets;
            CacheTotalSets = cacheStats.TotalSets;
            CacheTotalEvictions = cacheStats.TotalEvictions;

            // ── 心跳 ──
            HeartbeatRunning = _heartbeat.IsRunning;

            LastRefreshTime = DateTime.Now.ToString("HH:mm:ss");
        }
        catch
        {
            // 静默处理刷新异常，不阻塞 UI
        }
    }

    private static string ExtractDeviceId(string driverName)
    {
        var idx = driverName.IndexOf('-');
        return idx >= 0 ? driverName[(idx + 1)..] : driverName;
    }

    private static string GetDriverType(string driverName)
    {
        var idx = driverName.IndexOf('-');
        return idx >= 0 ? driverName[..idx] : driverName;
    }

    public void Dispose()
    {
        _refreshTimer?.Dispose();
    }
}

/// <summary>
/// 设备列表中的单项 ViewModel。
/// </summary>
public partial class DeviceItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _deviceId = string.Empty;

    [ObservableProperty]
    private string _driverName = string.Empty;

    [ObservableProperty]
    private string _driverType = string.Empty;

    [ObservableProperty]
    private ConnectionState _state;

    [ObservableProperty]
    private bool _isOnline;

    /// <summary>状态颜色标记（用于 UI 绑定）。</summary>
    public string StateColor => State switch
    {
        ConnectionState.Connected => "#4CAF50",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "#FF9800",
        ConnectionState.Disconnecting => "#2196F3",
        _ => "#9E9E9E"
    };

    /// <summary>状态文本。</summary>
    public string StateText => State switch
    {
        ConnectionState.Connected => "在线",
        ConnectionState.Connecting => "连接中",
        ConnectionState.Disconnecting => "断开中",
        ConnectionState.Reconnecting => "重连中",
        _ => "离线"
    };
}
