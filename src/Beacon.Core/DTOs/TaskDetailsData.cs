using Beacon.Core.Data.Enums;

namespace Beacon.Core.DTOs;

public record TaskDetailsData
{
    public required int Id { get; init; }
    public required SubscriptionSummary Subscription { get; init; }
    public required int LatestResultCount { get; init; }
    public DateTime? LastNotificationAt { get; init; }
    public required int NotificationCount { get; init; }
    public required List<NotificationSummary> Notifications { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required bool Resolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByUserId { get; init; }

    /// <summary>The resolver's display name, or "User {id}" without one (never the user name or external id).</summary>
    public string? ResolvedByUserName { get; init; }
    public string? ResolutionNotes { get; init; }
    public required int QueryId { get; init; }
    public required string QueryName { get; init; }

    /// <summary>
    /// Cron expression of the parent subscription, e.g. "0 */15 * * *".
    /// </summary>
    public string? CronExpression { get; init; }

    /// <summary>
    /// Timestamp of the most recent execution for the parent subscription, null if none.
    /// </summary>
    public DateTime? LastExecutionAt { get; init; }

    /// <summary>
    /// AI Actor ID if the subscription is managed by an AI Actor, null if user-created
    /// </summary>
    public int? AiActorId { get; init; }

    /// <summary>
    /// Name of the AI Actor managing the subscription
    /// </summary>
    public string? AiActorName { get; init; }

    public TaskPriority Priority { get; init; }

    public string? AssigneeUserId { get; init; }

    /// <summary>The assignee's display name, or "User {id}" without one (never the user name or external id).</summary>
    public string? AssigneeUserName { get; init; }

    public DateTime? SnoozedUntil { get; init; }

    public int? SlaHours { get; init; }

    public int WatcherCount { get; init; }

    public bool IsWatching { get; init; }

    /// <summary>The task's subscription is managed by an AI actor with a recorded creator.</summary>
    public bool HasOwner { get; init; }

    /// <summary>
    /// The AI actor creator's display name, or a neutral "User {id}" label without one (never the user name or external
    /// id); null when there is no creator or no matching user.
    /// </summary>
    public string? OwnerDisplayName { get; init; }
}

public record SubscriptionSummary(int Id, string Name, string? Description);
public record QueryExecutionSummary(int Id, DateTime ExecutedAt, double ExecutionTimeMs, NotificationStatus Status, int ResultCount);
public record NotificationSummary(int Id, DateTime SentAt, int ResultCount);
public record CommentData(int Id, string Content, string? UserName, DateTime CreatedAt);
public record RelatedTaskSummary(int Id, DateTime CreatedAt, int LatestResultCount, bool Resolved, DateTime? ResolvedAt);
public record ResultCountDataPoint(DateTime Date, int ResultCount);
