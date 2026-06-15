using Avalonia;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace App.Host;

internal static class Program
{
    private static void Main(string[] args)
    {
        // 配置 Serilog
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Console()
            .WriteTo.File(
                path: "logs/logistics-hmi-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30)
            .CreateLogger();

        try
        {
            Log.Information("LogisticsHMI 启动中...");

            // 构建 DI 容器
            var services = new ServiceCollection();
            services.AddLogisticsHmiServices();
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
