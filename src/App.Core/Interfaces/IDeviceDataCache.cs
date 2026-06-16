namespace App.Core;

/// <summary>
/// 设备数据缓存。
/// </summary>
public interface IDeviceDataCache
{
    /// <summary>
    /// 尝试读取缓存数据。
    /// </summary>
    bool TryGet(string deviceId, string address, out byte[]? data);

    /// <summary>
    /// 写入或更新缓存。
    /// </summary>
    void Set(string deviceId, string address, byte[] data, TimeSpan? ttl = null);

    /// <summary>
    /// 移除缓存。
    /// </summary>
    bool Remove(string deviceId, string address);

    /// <summary>
    /// 清空设备缓存。
    /// </summary>
    void Clear(string deviceId);
}
