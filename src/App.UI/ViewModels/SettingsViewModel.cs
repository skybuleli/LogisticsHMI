using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using App.Core;

namespace App.UI.ViewModels;

/// <summary>
/// 系统设置页面 ViewModel。
/// 显示和编辑应用配置（日志、窗口、主题、通信）。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly IConfigurationService _configService;

    public SettingsViewModel(IConfigurationService configService)
    {
        _configService = configService;
        Title = "系统设置";

        // 从配置加载当前值
        LoadFromConfig();
    }

    // ── 日志配置 ────────────────────────────────────────
    [ObservableProperty] private string _logLevel = "Information";
    [ObservableProperty] private int _mainLogRetentionDays = 30;
    [ObservableProperty] private int _commLogRetentionDays = 14;

    // ── 窗口配置 ────────────────────────────────────────
    [ObservableProperty] private string _windowTitle = "Logistics HMI — 物流上位机系统";
    [ObservableProperty] private bool _startMaximized = true;
    [ObservableProperty] private bool _minimizeToTrayOnClose = true;

    // ── 主题配置 ────────────────────────────────────────
    [ObservableProperty] private string _defaultTheme = "工业蓝";
    [ObservableProperty] private bool _allowThemeSwitch = true;

    // ── 通信配置 ────────────────────────────────────────
    [ObservableProperty] private int _defaultTimeoutMs = 5000;
    [ObservableProperty] private int _heartbeatIntervalMs = 30000;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private int _reconnectMaxRetries = 5;

    // ── 设备扫描配置 ────────────────────────────────────
    [ObservableProperty] private bool _scanEnabled = true;
    [ObservableProperty] private int _scanIntervalMs = 500;

    /// <summary>保存成功的提示消息。</summary>
    [ObservableProperty] private string _saveMessage = string.Empty;

    // ── 下拉选项 ────────────────────────────────────────
    public string[] LogLevelOptions { get; } = ["Debug", "Information", "Warning", "Error"];
    public string[] ThemeOptions { get; } = ["工业蓝", "暗黑", "高对比度"];

    /// <summary>加载配置到 UI。</summary>
    [RelayCommand]
    private void LoadConfig()
    {
        _configService.Reload();
        LoadFromConfig();
        SaveMessage = "已重新加载";
    }

    /// <summary>保存当前 UI 值到配置。</summary>
    [RelayCommand]
    private void SaveConfig()
    {
        var config = _configService.Current;

        config.Logging.MinimumLevel = LogLevel;
        config.Logging.MainLogRetentionDays = MainLogRetentionDays;
        config.Logging.CommLogRetentionDays = CommLogRetentionDays;

        config.Window.Title = WindowTitle;
        config.Window.StartMaximized = StartMaximized;
        config.Window.MinimizeToTrayOnClose = MinimizeToTrayOnClose;

        config.Theme.DefaultTheme = DefaultTheme;
        config.Theme.AllowThemeSwitch = AllowThemeSwitch;

        config.Communication.DefaultTimeoutMs = DefaultTimeoutMs;
        config.Communication.HeartbeatIntervalMs = HeartbeatIntervalMs;
        config.Communication.AutoReconnect = AutoReconnect;
        config.Communication.ReconnectMaxRetries = ReconnectMaxRetries;

        config.DeviceScan.Enabled = ScanEnabled;
        config.DeviceScan.IntervalMs = ScanIntervalMs;

        _configService.Save();
        SaveMessage = "✅ 配置已保存";
    }

    private void LoadFromConfig()
    {
        var c = _configService.Current;

        LogLevel = c.Logging.MinimumLevel;
        MainLogRetentionDays = c.Logging.MainLogRetentionDays;
        CommLogRetentionDays = c.Logging.CommLogRetentionDays;

        WindowTitle = c.Window.Title;
        StartMaximized = c.Window.StartMaximized;
        MinimizeToTrayOnClose = c.Window.MinimizeToTrayOnClose;

        DefaultTheme = c.Theme.DefaultTheme;
        AllowThemeSwitch = c.Theme.AllowThemeSwitch;

        DefaultTimeoutMs = c.Communication.DefaultTimeoutMs;
        HeartbeatIntervalMs = c.Communication.HeartbeatIntervalMs;
        AutoReconnect = c.Communication.AutoReconnect;
        ReconnectMaxRetries = c.Communication.ReconnectMaxRetries;

        ScanEnabled = c.DeviceScan.Enabled;
        ScanIntervalMs = c.DeviceScan.IntervalMs;
    }
}
