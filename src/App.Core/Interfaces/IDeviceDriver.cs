using System.IO.Pipelines;

namespace App.Core;

/// <summary>
/// 设备驱动统一接口。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    /// <summary>
    /// 驱动名称。
    /// </summary>
    string DriverName { get; }

    /// <summary>
    /// 驱动配置。
    /// </summary>
    DriverConfigBase Config { get; }

    /// <summary>
    /// 当前连接状态。
    /// </summary>
    ConnectionState State { get; }

    /// <summary>
    /// 连接状态变更事件。
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// 连接到设备。
    /// </summary>
    ValueTask<Result> ConnectAsync(CancellationToken ct = default);

    /// <summary>
    /// 断开连接。
    /// </summary>
    ValueTask<Result> DisconnectAsync(CancellationToken ct = default);

    /// <summary>
    /// 读取数据。
    /// </summary>
    /// <param name="address">地址，协议相关语义。</param>
    /// <param name="length">期望读取长度。</param>
    ValueTask<Result<byte[]>> ReadAsync(string address, ushort length, CancellationToken ct = default);

    /// <summary>
    /// 写入数据。
    /// </summary>
    /// <param name="address">地址，协议相关语义。</param>
    /// <param name="data">待写入二进制数据。</param>
    ValueTask<Result> WriteAsync(string address, byte[] data, CancellationToken ct = default);

    /// <summary>
    /// 订阅地址变化。
    /// </summary>
    /// <returns>Disposable 用于取消订阅。</returns>
    ValueTask<IDisposable> SubscribeAsync(string address, Action<byte[]> callback, CancellationToken ct = default);
}
