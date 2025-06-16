using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.QueryExecutionHistory;

public class QueryExecutionHistoryData
{
    public required int QueryExecutionHistoryId { get; set; }

    public required List<string> Recipients { get; set; }

    public required List<NotificationType> NotificationTypes { get; set; }

    public required int ResultCount { get; set; }

    public DateTime CreatedTime { get; set; }

    public NotificationStatus NotificationStatus { get; set; }

    public string QueryName { get; set; }

    public int SubscriptionId { get; set; }
    
    public double ExecutionTimeMs { get; set; }
}