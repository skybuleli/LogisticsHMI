using Microsoft.Extensions.Logging;
using App.Core;

namespace App.Infrastructure;

/// <summary>
/// 业务通知服务实现。
/// </summary>
public sealed class BusinessNotificationService : IBusinessNotificationService
{
    private readonly ILogger<BusinessNotificationService> _logger;
    private int _sequence;

    public BusinessNotificationService(ILogger<BusinessNotificationService> logger)
    {
        _logger = logger;
    }

    public event EventHandler<BusinessNotificationEventArgs>? NotificationReceived;

    public void SendNotification(string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        SendNotification(content, category: string.Empty);
    }

    public void SendNotification(string content, string category)
    {
        var args = BuildEventArgs(content, category);
        OnNotificationReceived(args);
        _logger.LogInformation("业务通知已广播：{Category} {Content}", category, content);
    }

    public bool TrySendNotification(string content, out string? error)
    {
        return TrySendNotification(content, category: string.Empty, out error);
    }

    public bool TrySendNotification(string content, string category, out string? error)
    {
        error = null;
        try
        {
            SendNotification(content, category);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "广播业务通知失败");
            error = ex.Message;
            return false;
        }
    }

    private BusinessNotificationEventArgs BuildEventArgs(string content, string? category)
    {
        var id = Interlocked.Increment(ref _sequence);
        return new BusinessNotificationEventArgs
        {
            MessageId = $"notify-{id:D8}-{Guid.NewGuid():N}",
            Content = content,
            Category = category
        };
    }

    private void OnNotificationReceived(BusinessNotificationEventArgs args)
    {
        Volatile.Read(ref NotificationReceived)?.Invoke(this, args);
    }
}
