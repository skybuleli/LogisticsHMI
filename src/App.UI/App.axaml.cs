using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using App.UI.Services;

namespace App.UI;

public partial class App : Application
{
    /// <summary>
    /// 全局 DI 容器。由 App.Host.Program 在启动时设置。
    /// </summary>
    public static IServiceProvider ServiceProvider { get; set; } = null!;

    /// <summary>
    /// 窗口管理服务。单例，在 OnFrameworkInitializationCompleted 中初始化。
    /// </summary>
    internal static IWindowService? WindowService { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 解析主窗口
            var mainWindow = ServiceProvider.GetRequiredService<MainWindow>();

            // 初始化窗口管理服务
            var windowService = ServiceProvider.GetRequiredService<IWindowService>();
            windowService.Initialize(mainWindow);
            WindowService = windowService;

            // 设置主窗口
            desktop.MainWindow = mainWindow;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
