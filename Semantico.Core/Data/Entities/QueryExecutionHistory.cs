using Semantico.Core.Abstractions;
using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

public class QueryExecutionHistory : BaseEntity, IExecutionHistory
{
    public required int SubscriptionId { get; set; }

    public required int ResultCount { get; set; }

    public required string CompiledSql { get; set; }

    public required NotificationStatus NotificationStatus { get; set; }

    public required double ExecutionTimeMs { get; set; }

    /// <summary>
    /// JSON serialized query results (stored when subscription.StoreResults is enabled)
    /// </summary>
    public string? Results { get; set; }

    /// <summary>
    /// Comment field for storing error messages or other notes
    /// </summary>
    public string? Comment { get; set; }

    public List<Notification> Notifications { get; set; } = new();

    public Subscription Subscription { get; set; } = null!;

    // IExecutionHistory implementation
    DateTime IExecutionHistory.StartedAt => CreatedTime;
    DateTime? IExecutionHistory.CompletedAt => CreatedTime.AddMilliseconds(ExecutionTimeMs);
    TimeSpan IExecutionHistory.ExecutionDuration => TimeSpan.FromMilliseconds(ExecutionTimeMs);
    bool IExecutionHistory.Success => NotificationStatus == NotificationStatus.NotificationSent;
    string? IExecutionHistory.ErrorMessage => Comment ?? (NotificationStatus == NotificationStatus.Timeout
        ? "Query execution timed out"
        : NotificationStatus != NotificationStatus.NotificationSent
            ? $"Notification status: {NotificationStatus}"
            : null);
}
