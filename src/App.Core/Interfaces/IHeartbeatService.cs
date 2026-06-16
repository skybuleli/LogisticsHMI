namespace App.Core;

/// <summary>
/// 心跳服务接口。
/// <para>
/// 负责对所有已注册设备进行定期心跳检查，跟踪每台设备的心跳状态，
/// 在设备心跳丢失或恢复时发出事件通知。
/// 心跳检查 ≠ 健康检查 — 心跳是轻量级连接存活验证，健康检查包含更深入的状态检测。
/// </para>
/// </summary>
public interface IHeartbeatService
{
    /// <summary>心跳服务是否正在运行。</summary>
    bool IsRunning { get; }

    /// <summary>
    /// 全局心跳 Tick 事件。每次心跳周期触发一次。
    /// 包含所有设备的心跳状态快照，UI 层可绑定此事件更新状态指示器。
    /// </summary>
    event EventHandler<HeartbeatTickEventArgs>? HeartbeatTick;

    /// <summary>
    /// 设备心跳丢失事件。
    /// 当某设备连续 N 次（<see cref="HeartbeatConfig.MissedThreshold"/>）心跳检查失败时触发。
    /// </summary>
    event EventHandler<HeartbeatDeviceEventArgs>? HeartbeatMissed;

    /// <summary>
    /// 设备心跳恢复事件。
    /// 在设备心跳丢失后，首次心跳检查成功时触发。
    /// </summary>
    event EventHandler<HeartbeatDeviceEventArgs>? HeartbeatRecovered;

    /// <summary>
    /// 启动后台心跳检查。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// 停止后台心跳检查。
    /// </summary>
    Task StopAsync();

    /// <summary>
    /// 立即执行一次心跳检查。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>当前心跳快照。</returns>
    Task<HeartbeatSnapshot> PulseAsync(CancellationToken ct = default);

    /// <summary>
    /// 获取当前所有设备心跳状态快照（不触发检查）。
    /// </summary>
    Task<HeartbeatSnapshot> GetSnapshotAsync(CancellationToken ct = default);
}
