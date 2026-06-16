using System;

namespace App.Core;

/// <summary>
/// 配置管理服务接口。
/// 提供应用配置的加载、访问、保存和热重载。
/// </summary>
public interface IConfigurationService
{
    /// <summary>应用完整配置。</summary>
    AppConfig Current { get; }

    /// <summary>日志配置。</summary>
    LoggingConfig Logging { get; }

    /// <summary>窗口配置。</summary>
    WindowConfig Window { get; }

    /// <summary>主题配置。</summary>
    ThemeConfig Theme { get; }

    /// <summary>通信配置。</summary>
    CommunicationConfig Communication { get; }

    /// <summary>设备扫描配置。</summary>
    DeviceScanConfig DeviceScan { get; }

    /// <summary>配置变更事件（热重载时触发）。</summary>
    event EventHandler<ConfigChangedEventArgs>? ConfigChanged;

    /// <summary>重新加载配置文件。</summary>
    void Reload();

    /// <summary>保存当前配置到文件。</summary>
    void Save();
}

/// <summary>
/// 配置变更事件参数。
/// </summary>
public class ConfigChangedEventArgs : EventArgs
{
    /// <summary>发生变更的配置节名称。</summary>
    public string[] ChangedSections { get; init; } = [];
}
