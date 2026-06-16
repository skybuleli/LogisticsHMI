namespace App.Core;

/// <summary>
/// 设备连接状态。
/// </summary>
public enum ConnectionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Disconnecting = 3,
    Reconnecting = 4
}
