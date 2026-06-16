namespace App.Core;

/// <summary>
/// 设备健康状态。
/// </summary>
public enum DeviceHealth
{
    Online = 0,
    Idle = 1,
    Busy = 2,
    Warning = 3,
    Fault = 4,
    Maintenance = 5,
    Offline = 6
}
