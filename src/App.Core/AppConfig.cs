namespace App.Core;

/// <summary>
/// 应用根配置（映射 appsettings.json 根节点）。
/// </summary>
public class AppConfig
{
    public const string SectionName = "";

    public LoggingConfig Logging { get; set; } = new();
    public WindowConfig Window { get; set; } = new();
    public ThemeConfig Theme { get; set; } = new();
    public CommunicationConfig Communication { get; set; } = new();
    public DeviceScanConfig DeviceScan { get; set; } = new();
    public ConnectionPoolConfig ConnectionPool { get; set; } = new();
    public List<DeviceConfigEntry> Devices { get; set; } = [];
}

/// <summary>
/// 日志配置。
/// </summary>
public class LoggingConfig
{
    public const string SectionName = "Logging";

    public string MinimumLevel { get; set; } = "Information";
    public string MainLogPath { get; set; } = "logs/logistics-hmi-.log";
    public string CommLogPath { get; set; } = "logs/comm-.log";
    public int MainLogRetentionDays { get; set; } = 30;
    public int CommLogRetentionDays { get; set; } = 14;
}

/// <summary>
/// 主窗口配置。
/// </summary>
public class WindowConfig
{
    public const string SectionName = "Window";

    public string Title { get; set; } = "Logistics HMI — 物流上位机系统";
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 800;
    public int MinWidth { get; set; } = 1024;
    public int MinHeight { get; set; } = 600;
    public bool StartMaximized { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
}

/// <summary>
/// 主题配置。
/// </summary>
public class ThemeConfig
{
    public const string SectionName = "Theme";

    public string DefaultTheme { get; set; } = "工业蓝";
    public bool AllowThemeSwitch { get; set; } = true;
}

/// <summary>
/// 通信层默认参数。
/// </summary>
public class CommunicationConfig
{
    public const string SectionName = "Communication";

    public int DefaultTimeoutMs { get; set; } = 5000;
    public int HeartbeatIntervalMs { get; set; } = 30000;
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectMaxRetries { get; set; } = 5;
    public int ReconnectBaseDelayMs { get; set; } = 1000;
}

/// <summary>
/// 设备扫描参数。
/// </summary>
public class DeviceScanConfig
{
    public const string SectionName = "DeviceScan";

    public bool Enabled { get; set; } = true;
    public int IntervalMs { get; set; } = 500;
    public int CacheTtlMs { get; set; } = 200;
}
