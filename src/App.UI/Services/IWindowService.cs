using System;

namespace App.UI.Services;

/// <summary>
/// 窗口管理服务接口。
/// 提供主窗口显示/隐藏、最小化到托盘、窗口状态管理等功能。
/// </summary>
public interface IWindowService
{
    /// <summary>主窗口是否可见。</summary>
    bool IsVisible { get; }

    /// <summary>主窗口是否已最小化到托盘。</summary>
    bool IsMinimizedToTray { get; }

    /// <summary>显示主窗口（从托盘恢复）。</summary>
    void Show();

    /// <summary>隐藏主窗口（最小化到托盘）。</summary>
    void Hide();

    /// <summary>切换主窗口可见性。</summary>
    void ToggleVisibility();

    /// <summary>最小化窗口。</summary>
    void Minimize();

    /// <summary>最大化窗口。</summary>
    void Maximize();

    /// <summary>还原窗口。</summary>
    void Restore();

    /// <summary>初始化窗口服务（获取主窗口引用并注册事件）。</summary>
    void Initialize(Avalonia.Controls.Window mainWindow);

    /// <summary>关闭应用程序。</summary>
    void Shutdown();

    /// <summary>窗口状态改变事件。</summary>
    event EventHandler<WindowStateChangedEventArgs>? WindowStateChanged;
}

/// <summary>
/// 窗口状态改变事件参数。
/// </summary>
public class WindowStateChangedEventArgs : EventArgs
{
    public bool IsVisible { get; init; }
    public bool IsMinimized { get; init; }
}
