using Microsoft.Extensions.DependencyInjection;
using App.UI;
using App.UI.Services;
using App.UI.ViewModels;

namespace App.Host;

/// <summary>
/// 依赖注入服务的统一注册入口。
/// 后续每个 Phase 添加新服务时在此集中配置生命周期。
/// </summary>
internal static class ServiceCollectionExtensions
{
    /// <summary>
    /// 注册 LogisticsHMI 系统的所有服务。
    /// </summary>
    public static IServiceCollection AddLogisticsHmiServices(this IServiceCollection services)
    {
        // ── Views ───────────────────────────────────────────
        services.AddTransient<MainWindow>();

        // ── ViewModels ──────────────────────────────────────
        services.AddTransient<HomeViewModel>();

        // ── UI 服务 ─────────────────────────────────────────
        services.AddSingleton<ThemeService>();

        // ── 基础设施服务（后续 Phase 启用）────────────────────
        // services.AddSingleton<IDeviceManager, DeviceManager>();
        // services.AddSingleton<INavigationService, NavigationService>();
        // services.AddSingleton<IAlarmService, AlarmService>();

        return services;
    }
}
