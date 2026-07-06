using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.QueryExecutionHistory;

public class NotificationDetailsData
{
    public int Id { get; set; }

    public DateTime CreatedTime { get; set; }

    public DateTime SentAt { get; set; }

    public NotificationType Type { get; set; }

    public string? Results { get; set; }

    public string RecipientName { get; set; } = null!;

    public string QueryName { get; set; } = null!;

    public int QueryId { get; set; }

    public int SubscriptionId { get; set; }

    public double ExecutionTimeMs { get; set; }

    public int? ResultCount { get; set; }

    public NotificationStatus NotificationStatus { get; set; }
}