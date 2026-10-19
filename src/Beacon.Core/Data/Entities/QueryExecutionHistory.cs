using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class QueryExecutionHistory : BaseEntity
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

    /// <summary>
    /// The data sources the run read, recorded when it ran. Null for runs recorded before Beacon kept them: such a run is
    /// not readable by a project-restricted caller.
    /// </summary>
    public int[]? DataSourceIds { get; set; }

    public List<Notification> Notifications { get; set; } = new();

    public Subscription Subscription { get; set; } = null!;
}
