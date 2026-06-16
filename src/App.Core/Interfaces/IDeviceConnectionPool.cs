namespace App.Core;

/// <summary>
/// 设备连接池接口。
/// <para>
/// 管理多个设备驱动的生命周期：创建、复用、健康检查、限流、释放。
/// </para>
/// </summary>
public interface IDeviceConnectionPool : IAsyncDisposable
{
    /// <summary>
    /// 获取指定设备的驱动。如果驱动尚未创建，将使用工厂创建。
    /// </summary>
    /// <param name="deviceId">设备唯一标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>设备驱动实例。如果设备未配置，返回失败结果。</returns>
    Task<Result<IDeviceDriver>> GetDriverAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// 获取所有已注册的设备驱动。
    /// </summary>
    /// <returns>所有设备驱动列表（包括未激活的）。</returns>
    IReadOnlyList<IDeviceDriver> GetAllDrivers();

    /// <summary>
    /// 移除并释放指定设备的驱动。
    /// </summary>
    /// <param name="deviceId">设备唯一标识。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>是否成功移除。</returns>
    Task<bool> RemoveDriverAsync(string deviceId, CancellationToken ct = default);

    /// <summary>
    /// 启动后台健康检查。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止后台健康检查。
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 立即执行一次健康检查。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>健康检查结果。</returns>
    Task<HealthCheckResult> CheckHealthAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取连接池统计信息。
    /// </summary>
    ConnectionPoolStats GetStats();

    /// <summary>
    /// 连接池事件（连接、断开、健康检查异常等）。
    /// </summary>
    event EventHandler<ConnectionPoolEventArgs>? PoolEvent;
}
