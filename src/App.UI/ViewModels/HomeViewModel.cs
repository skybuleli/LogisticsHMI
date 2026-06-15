using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using App.Core;
using App.UI.Services;

namespace App.UI.ViewModels;

/// <summary>
/// 主页仪表盘 ViewModel。
/// 演示 CommunityToolkit.Mvvm 源码生成器功能：
/// [ObservableProperty] / [RelayCommand] / partial 类 / IDisposable
/// </summary>
public partial class HomeViewModel : ViewModelBase, IDisposable
{
    private static readonly Random _rng = new();
    private readonly System.Timers.Timer _timer;
    private readonly ThemeService _themeService;

    public HomeViewModel(ThemeService themeService)
    {
        _themeService = themeService;
        Title = "仪表盘";
        _currentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        _currentThemeName = _themeService.CurrentThemeName;

        // 每秒更新一次时间
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) =>
        {
            // 在 UI 线程上更新
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CurrentTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            });
        };
        _timer.Start();
    }

    [ObservableProperty]
    private string _currentTime;

    [ObservableProperty]
    private int _onlineDevices = 12;

    [ObservableProperty]
    private int _runningTasks = 3;

    [ObservableProperty]
    private int _todayCompletedTasks = 47;

    [ObservableProperty]
    private int _activeAlarms = 1;

    [ObservableProperty]
    private string _currentThemeName;

    /// <summary>
    /// 刷新命令 — 手动触发数据刷新。
    /// </summary>
    [RelayCommand]
    private void Refresh()
    {
        // 模拟刷新：随机生成示例数据
        OnlineDevices = _rng.Next(8, 16);
        RunningTasks = _rng.Next(1, 8);
        TodayCompletedTasks = _rng.Next(30, 80);
        ActiveAlarms = _rng.Next(0, 5);
        Title = $"仪表盘 (已刷新 {DateTime.Now:HH:mm:ss})";
    }

    /// <summary>
    /// 显示关于对话框命令。
    /// </summary>
    [RelayCommand]
    private void ShowAbout()
    {
        SetError("物流上位机系统 v0.1 — Phase 1 建设中");
    }

    /// <summary>
    /// 清除错误。
    /// </summary>
    [RelayCommand]
    private void DismissError()
    {
        ClearError();
    }

    /// <summary>
    /// 切换主题命令（循环切换）。
    /// </summary>
    [RelayCommand]
    private void SwitchTheme()
    {
        _themeService.SwitchToNextTheme();
        CurrentThemeName = _themeService.CurrentThemeName;
        Title = $"仪表盘 · {CurrentThemeName}";
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
