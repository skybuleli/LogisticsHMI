namespace App.Core;

/// <summary>
/// 业务状态和通知消息服务。
/// </summary>
public interface IBusinessNotificationService
{
    /// <summary>
    /// 发送通知给其他用户。
    /// </summary>
    void SendNotification(string content);

    /// <summary>
    /// 发送分类通知。
    /// </summary>
    void SendNotification(string content, string category);

    /// <summary>
    /// 发送通知并接收回执。
    /// </summary>
    bool TrySendNotification(string content, out string? error);

    /// <summary>
    /// 发送分类通知并接收回执。
    /// </summary>
    bool TrySendNotification(string content, string category, out string? error);

    /// <summary>
    /// 通知事件，其他用户可订阅。
    /// </summary>
    event EventHandler<BusinessNotificationEventArgs>? NotificationReceived;
}

/// <summary>
/// 通知事件参数。
/// </summary>
public sealed class BusinessNotificationEventArgs : EventArgs
{
    public required string MessageId { get; init; }
    public required string Content { get; init; }
    public string? Category { get; init; }
    public DateTimeOffset ReceivedAt { get; init; } = DateTimeOffset.UtcNow;
}
