using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.QueryExecutionHistory;

public class QueryExecutionHistoryData
{
    public required int QueryExecutionHistoryId { get; set; }

    public required string RecipientName { get; set; }

    public required NotificationType NotificationType { get; set; }

    public required int ResultCount { get; set; }

    public DateTime CreatedTime { get; set; }

    public bool NotificationSent { get; set; }

    public string QueryName { get; set; }

    public int SubscriptionId { get; set; }
}