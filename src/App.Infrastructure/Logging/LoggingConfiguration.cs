using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace App.Infrastructure.Logging;

/// <summary>
/// Serilog 日志系统的统一配置入口。
/// 管理主日志、通信日志、控制台输出三个 Sink。
/// </summary>
public static class LoggingConfiguration
{
    /// <summary>
    /// 主日志文件路径模式（按天滚动，默认保留 30 天）。
    /// </summary>
    private const string MainLogPath = "logs/logistics-hmi-.log";

    /// <summary>
    /// 通信日志文件路径模式（独立文件，按天滚动，默认保留 14 天）。
    /// </summary>
    private const string CommLogPath = "logs/comm-.log";

    /// <summary>
    /// 判断日志事件是否来自通信模块（通过 SourceContext 判断）。
    /// </summary>
    private static bool IsCommunicationLog(LogEvent evt)
    {
        return evt.Properties.TryGetValue("SourceContext", out var ctx)
            && ctx.ToString()?.Contains("Communication", StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// 配置并创建 Serilog 根 Logger。
    /// </summary>
    public static Logger CreateLogger(LoggingLevelSwitch? levelSwitch = null)
    {
        var config = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("System", LogEventLevel.Warning);

        if (levelSwitch != null)
            config = config.MinimumLevel.ControlledBy(levelSwitch);

        return config
            // ── 控制台 Sink（仅 Info+，短格式适合终端）──
            .WriteTo.Console(
                restrictedToMinimumLevel: LogEventLevel.Information,
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            // ── 主日志 Sink（仅 Info+，全格式保留 30 天）──
            .WriteTo.File(
                path: MainLogPath,
                restrictedToMinimumLevel: LogEventLevel.Information,
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 30,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            // ── 通信日志 Sink（仅通信模块日志，Debug+ 级别，保留 14 天）──
            .WriteTo.Logger(subLogger => subLogger
                .Filter.ByIncludingOnly(IsCommunicationLog)
                .WriteTo.File(
                    path: CommLogPath,
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 14,
                    outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"))
            .Enrich.FromLogContext()
            .CreateLogger();
    }
}
