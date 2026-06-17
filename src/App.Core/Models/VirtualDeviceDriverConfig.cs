namespace App.Core;

/// <summary>
/// 虚拟设备驱动配置。
/// </summary>
public sealed class VirtualDeviceDriverConfig : DriverConfigBase
{
    /// <summary>
    /// 虚拟设备类型标签，仅用于区分用途和展示。
    /// </summary>
    public string DeviceProfile { get; set; } = "Generic";
}
