using System;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using App.Infrastructure.Logging;
using App.UI.Services;
using App.Core;
using App.Infrastructure;

namespace App.Host;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 单实例检测
        using var instanceGuard = new SingleInstanceGuard();
        if (!instanceGuard.TryAcquire())
        {
            SingleInstanceGuard.NotifyExistingInstance();
            Console.Error.WriteLine("已有实例正在运行，当前实例退出。");
            return;
        }

        // 创建 Serilog 级别开关，支持运行时动态调整
        var levelSwitch = new LoggingLevelSwitch();

        // 配置 Serilog（主日志 + 通信日志 + 控制台）
        Log.Logger = LoggingConfiguration.CreateLogger(levelSwitch);

        try
        {
            Log.Information("LogisticsHMI 启动中...");
            Log.Information("日志系统：主日志=logs/logistics-hmi-.log, 通信日志=logs/comm-.log");

            // 构建 DI 容器
            var services = new ServiceCollection();
            services.AddSingleton(levelSwitch);
            services.AddLogisticsHmiServices();
            services.AddLogging(builder => builder.AddSerilog(dispose: true));
            var serviceProvider = services.BuildServiceProvider();

            // 将 DI 容器注入 Avalonia App
            App.UI.App.ServiceProvider = serviceProvider;

            Log.Information("DI 容器已构建");

            // 从配置读取日志级别并应用到 Serilog（使 appsettings.json 的 MinimumLevel 实时生效）
            ApplyLogLevelFromConfig(serviceProvider, levelSwitch);

            // 构建并启动 Avalonia 应用
            BuildAvaloniaApp()
                .StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用程序启动失败");
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    /// <summary>
    /// 从 IConfigurationService 读取 MinimumLevel 并应用到 Serilog LevelSwitch。
    /// </summary>
    private static void ApplyLogLevelFromConfig(IServiceProvider serviceProvider, LoggingLevelSwitch levelSwitch)
    {
        try
        {
            var configurationService = serviceProvider.GetService<App.Core.IConfigurationService>();
            var configuredLevel = configurationService?.Logging?.MinimumLevel;

            if (!string.IsNullOrWhiteSpace(configuredLevel) &&
                Enum.TryParse<LogEventLevel>(configuredLevel, true, out var level))
            {
                levelSwitch.MinimumLevel = level;
                Log.Information("日志级别已从配置生效：{Level}", level);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "应用配置日志级别失败，使用默认级别");
        }
    }

    /// <summary>
    /// 构建 Avalonia 应用程序。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App.UI.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
