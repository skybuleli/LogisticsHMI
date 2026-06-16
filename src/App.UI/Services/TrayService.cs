using System;
using Avalonia.Controls;
using Avalonia.Platform;

namespace App.UI.Services;

/// <summary>
/// 系统托盘服务。
/// 管理托盘图标、右键菜单、通知气泡。
/// </summary>
public class TrayService : IDisposable
{
    private readonly IWindowService _windowService;
    private TrayIcon? _trayIcon;
    private NativeMenuItem? _showItem;
    private NativeMenuItem? _hideItem;
    private NativeMenuItem? _exitItem;
    private bool _disposed;

    public TrayService(IWindowService windowService)
    {
        _windowService = windowService;
    }

    /// <summary>
    /// 初始化托盘图标和菜单。
    /// </summary>
    public void Setup()
    {
        if (_trayIcon != null) return;

        _showItem = new NativeMenuItem("显示主窗口");
        _showItem.Click += OnShowClicked;

        _hideItem = new NativeMenuItem("隐藏到托盘");
        _hideItem.Click += OnHideClicked;

        var separator = new NativeMenuItemSeparator();

        _exitItem = new NativeMenuItem("退出");
        _exitItem.Click += OnExitClicked;

        var menu = new NativeMenu();
        menu.Add(_showItem);
        menu.Add(_hideItem);
        menu.Add(separator);
        menu.Add(_exitItem);

        _trayIcon = new TrayIcon
        {
            ToolTipText = "Logistics HMI — 物流上位机系统",
            Menu = menu,
            IsVisible = true,
        };

        // 尝试加载应用图标（如果存在）
        try
        {
            var iconStream = AssetLoader.Open(new Uri("avares://App.UI/Assets/app-icon.png"));
            _trayIcon.Icon = new WindowIcon(iconStream);
        }
        catch
        {
            // 无自定义图标时使用默认系统图标
        }

        _trayIcon.Clicked += OnTrayIconClicked;
    }

    private void OnShowClicked(object? sender, EventArgs e)
    {
        _windowService.Show();
    }

    private void OnHideClicked(object? sender, EventArgs e)
    {
        _windowService.Hide();
    }

    private void OnExitClicked(object? sender, EventArgs e)
    {
        if (_trayIcon != null)
            _trayIcon.IsVisible = false;

        _windowService.Shutdown();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        _windowService.ToggleVisibility();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_trayIcon != null)
        {
            _trayIcon.IsVisible = false;
            _trayIcon.Clicked -= OnTrayIconClicked;
            _trayIcon.Dispose();
            _trayIcon = null;
        }

        // 取消菜单事件订阅，防止内存泄漏
        if (_showItem != null)
        {
            _showItem.Click -= OnShowClicked;
            _showItem = null;
        }
        if (_hideItem != null)
        {
            _hideItem.Click -= OnHideClicked;
            _hideItem = null;
        }
        if (_exitItem != null)
        {
            _exitItem.Click -= OnExitClicked;
            _exitItem = null;
        }
    }
}
