namespace App.Core;

/// <summary>
/// 设备驱动工厂接口。
/// <para>
/// 根据设备配置条目创建对应的 <see cref="IDeviceDriver"/> 实例。
/// </para>
/// </summary>
public interface IDeviceDriverFactory
{
    /// <summary>
    /// 根据设备配置创建设备驱动实例。
    /// </summary>
    /// <param name="config">设备配置条目（含 DriverType 区分器）。</param>
    /// <returns>设备驱动实例。</returns>
    IDeviceDriver Create(DeviceConfigEntry config);
}
