using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using App.Infrastructure.Logging;

namespace App.Host;

internal static class Program
{
    private static void Main(string[] args)
    {
        // 配置 Serilog（主日志 + 通信日志 + 控制台）
        Log.Logger = LoggingConfiguration.CreateLogger();

        try
        {
            Log.Information("LogisticsHMI 启动中...");
            Log.Information("日志系统：主日志=logs/logistics-hmi-.log, 通信日志=logs/comm-.log");

            // 构建 DI 容器
            var services = new ServiceCollection();
            services.AddLogisticsHmiServices();
            // 注册 Serilog ILogger 到 DI（所有注入 ILogger<T> 的服务都可使用）
            services.AddLogging(builder => builder.AddSerilog(dispose: true));
            var serviceProvider = services.BuildServiceProvider();

            // 将 DI 容器注入 Avalonia App
            App.UI.App.ServiceProvider = serviceProvider;

            Log.Information("DI 容器已构建");

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
    /// 构建 Avalonia 应用程序。
    /// </summary>
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App.UI.App>()
            .UsePlatformDetect()
            .LogToTrace();
}
