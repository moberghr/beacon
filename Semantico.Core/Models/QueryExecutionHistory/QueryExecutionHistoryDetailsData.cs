using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.QueryExecutionHistory;

public class QueryExecutionHistoryDetailsData
{
    public int Id { get; set; }

    public DateTime CreatedTime { get; set; }

    public NotificationStatus NotificationStatus { get; set; }

    public double ExecutionTimeMs { get; set; }

    public int ResultCount { get; set; }

    public string CompiledSql { get; set; } = null!;

    public string? Results { get; set; }

    public string? Comment { get; set; }

    // Query and Subscription info
    public string QueryName { get; set; } = null!;

    public int QueryId { get; set; }

    public int SubscriptionId { get; set; }

    // Related entities
    public List<NotificationSummaryData> Notifications { get; set; } = new();

    public List<TaskSummaryData> Tasks { get; set; } = new();
}

public class NotificationSummaryData
{
    public int Id { get; set; }

    public string RecipientName { get; set; } = null!;

    public NotificationType Type { get; set; }

    public DateTime SentAt { get; set; }
}

public class TaskSummaryData
{
    public int Id { get; set; }

    public int LatestResultCount { get; set; }

    public DateTime CreatedAt { get; set; }

    public bool Resolved { get; set; }

    public DateTime? ResolvedAt { get; set; }
}