namespace App.Core;

/// <summary>
/// 业务通知消息。
/// </summary>
public sealed class BusinessNotificationMessage
{
    public required string Id { get; init; }
    public required string SenderId { get; init; }
    public required string Content { get; init; }
    public string? Category { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
