using System;
using CommunityToolkit.Mvvm.ComponentModel;
using App.Core;

namespace App.UI.ViewModels;

/// <summary>
/// 底部状态栏 ViewModel。
/// 显示系统连接状态、运行状态、当前时间等信息。
/// </summary>
public partial class StatusBarViewModel : ViewModelBase, IDisposable
{
    private readonly System.Timers.Timer _timer;

    public StatusBarViewModel()
    {
        _currentTime = DateTime.Now.ToString("HH:mm:ss");

        // 每秒更新时间
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) =>
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                CurrentTime = DateTime.Now.ToString("HH:mm:ss");
            });
        };
        _timer.Start();
    }

    /// <summary>连接状态文本（已连接/断开/重连中…）。</summary>
    [ObservableProperty]
    private string _connectionStatus = "已连接";

    /// <summary>系统运行状态。</summary>
    [ObservableProperty]
    private string _systemState = "运行正常";

    /// <summary>当前执行操作描述。</summary>
    [ObservableProperty]
    private string _currentOperation = "就绪";

    /// <summary>当前系统时间。</summary>
    [ObservableProperty]
    private string _currentTime;

    public void Dispose()
    {
        _timer?.Stop();
        _timer?.Dispose();
    }
}
