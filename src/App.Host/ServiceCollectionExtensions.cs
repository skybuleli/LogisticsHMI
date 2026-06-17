using Microsoft.Extensions.DependencyInjection;
using App.Core;
using App.Infrastructure;
using App.Infrastructure.Drivers;
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
        services.AddTransient<ShellViewModel>();
        services.AddTransient<HomeViewModel>();
        services.AddTransient<MonitorViewModel>();
        services.AddTransient<SettingsViewModel>();

        // ── 导航服务 ─────────────────────────────────────────
        services.AddSingleton<INavigationService, NavigationService>();

        // ── UI 服务 ─────────────────────────────────────────
        services.AddSingleton<ThemeService>();
        services.AddSingleton<IDialogService, DialogService>();
        services.AddSingleton<IWindowService, WindowService>();

        // ── 配置服务 ─────────────────────────────────────────
        services.AddSingleton<IConfigurationService, ConfigurationService>();

        // ── Phase 2 通信服务 ──────────────────────────────────
        // 驱动工厂 + 连接池管理
        services.AddSingleton<IDeviceDriverFactory, DeviceDriverFactory>();
        services.AddSingleton<IDeviceConnectionPool, ConnectionPoolManager>();

        // Phase 2.8 心跳服务
        services.AddSingleton<IHeartbeatService, HeartbeatService>();

        // Phase 2.9 设备数据缓存
        services.AddSingleton<IDeviceDataCache, DeviceDataCache>();

        // 按设备配置列表注册驱动（通过连接池自动管理生命周期）
        // 注：单个驱动 Transient 注册保留以供直接使用，
        // 生产环境应通过 IDeviceConnectionPool.GetDriverAsync(deviceId) 获取。
        services.AddTransient<ModbusTcpDriver>();
        services.AddTransient<ModbusRtuDriver>();
        services.AddTransient<S7Driver>();
        services.AddTransient<OpcUaDriver>();
        services.AddTransient<MqttDriver>();
        services.AddTransient<VirtualDeviceDriver>();

        return services;
    }
}
