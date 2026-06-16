using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;

namespace App.UI.Services;

/// <summary>
/// 窗口管理服务实现。
/// 提供主窗口的显示/隐藏、最小化到托盘、状态管理。
/// </summary>
public class WindowService : IWindowService, IDisposable
{
    private readonly TrayService? _trayService;
    private Window? _mainWindow;

    public WindowService()
    {
        try
        {
            _trayService = new TrayService(this);
        }
        catch
        {
            // 托盘在某些环境下可能不可用（如无桌面环境）
            _trayService = null;
        }
    }

    public bool IsVisible => _mainWindow?.IsVisible ?? false;

    public bool IsMinimizedToTray { get; private set; }

    public event EventHandler<WindowStateChangedEventArgs>? WindowStateChanged;

    /// <summary>
    /// 初始化窗口服务，获取主窗口引用并注册事件。
    /// </summary>
    public void Initialize(Window mainWindow)
    {
        _mainWindow = mainWindow;
        _mainWindow.Closing += OnMainWindowClosing;

        _trayService?.Setup();
    }

    public void Show()
    {
        if (_mainWindow == null) return;

        if (IsMinimizedToTray)
        {
            _mainWindow.Show();
            _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            IsMinimizedToTray = false;
            NotifyStateChanged();
        }
        else if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
            _mainWindow.Activate();
            NotifyStateChanged();
        }
    }

    public void Hide()
    {
        if (_mainWindow == null || !_mainWindow.IsVisible)
            return;

        _mainWindow.Hide();
        IsMinimizedToTray = true;
        NotifyStateChanged();
    }

    public void ToggleVisibility()
    {
        if (IsMinimizedToTray || !IsVisible)
            Show();
        else
            Hide();
    }

    public void Minimize()
    {
        if (_mainWindow == null) return;
        _mainWindow.WindowState = WindowState.Minimized;
    }

    public void Maximize()
    {
        if (_mainWindow == null) return;
        _mainWindow.WindowState = _mainWindow.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    public void Restore()
    {
        if (_mainWindow == null) return;
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Show();
        _mainWindow.Activate();
    }

    public void Shutdown()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private void OnMainWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        // 点击关闭按钮时最小化到托盘而非退出
        // 如果是系统关闭（关机/注销）则不拦截
        if (_trayService != null)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private void NotifyStateChanged()
    {
        WindowStateChanged?.Invoke(this, new WindowStateChangedEventArgs
        {
            IsVisible = IsVisible,
            IsMinimized = IsMinimizedToTray,
        });
    }

    public void Dispose()
    {
        _trayService?.Dispose();
        if (_mainWindow != null)
            _mainWindow.Closing -= OnMainWindowClosing;
    }
}
