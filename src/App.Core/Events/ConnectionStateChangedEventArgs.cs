namespace App.Core;

/// <summary>
/// 连接状态变更事件参数。
/// </summary>
public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState oldState, ConnectionState newState, string? detail = null)
    {
        OldState = oldState;
        NewState = newState;
        Detail = detail ?? string.Empty;
    }

    public ConnectionState OldState { get; }
    public ConnectionState NewState { get; }
    public string Detail { get; }
}
