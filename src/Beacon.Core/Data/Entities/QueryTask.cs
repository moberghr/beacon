using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class QueryTask : ArchivableBaseEntity
{
    public required int SubscriptionId { get; set; }
    public required int LatestResultCount { get; set; }
    public DateTime? LastNotificationAt { get; set; }
    public bool Resolved { get; set; } = false;
    public DateTime? ResolvedAt { get; set; }
    public string? ResolvedByUserId { get; set; }
    public string? ResolutionNotes { get; set; }

    /// <summary>
    /// User assigned to investigate/resolve this task. Null = unassigned.
    /// </summary>
    public string? AssigneeUserId { get; set; }

    /// <summary>
    /// When set in the future, the task is considered snoozed until this time.
    /// </summary>
    public DateTime? SnoozedUntil { get; set; }

    /// <summary>
    /// Priority level. Defaults to Normal.
    /// </summary>
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;

    // Navigation properties
    public List<Notification> Notifications { get; set; } = new();
    public Subscription Subscription { get; set; } = null!;
    public List<TaskWatcher> Watchers { get; set; } = new();
}
