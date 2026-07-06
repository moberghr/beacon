using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.QueryExecutionHistory;

public class QueryExecutionHistoryData
{
    public required int QueryExecutionHistoryId { get; set; }

    public required List<NotificationData> Notifications { get; set; }

    public required int ResultCount { get; set; }

    public DateTime CreatedTime { get; set; }

    public NotificationStatus NotificationStatus { get; set; }

    public string QueryName { get; set; }

    public int SubscriptionId { get; set; }

    public double ExecutionTimeMs { get; set; }

    public string? Comment { get; set; }

    /// <summary>
    /// AI Actor ID if the subscription is managed by an AI Actor, null if user-created
    /// </summary>
    public int? AiActorId { get; set; }

    /// <summary>
    /// Name of the AI Actor managing the subscription
    /// </summary>
    public string? AiActorName { get; set; }
}