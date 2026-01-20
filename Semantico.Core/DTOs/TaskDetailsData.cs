using Semantico.Core.Data.Enums;

namespace Semantico.Core.DTOs;

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
    public string? ResolvedByUserName { get; init; }
    public string? ResolutionNotes { get; init; }
    public required int QueryId { get; init; }
    public required string QueryName { get; init; }

    /// <summary>
    /// AI Actor ID if the subscription is managed by an AI Actor, null if user-created
    /// </summary>
    public int? AiActorId { get; init; }

    /// <summary>
    /// Name of the AI Actor managing the subscription
    /// </summary>
    public string? AiActorName { get; init; }
}

public record SubscriptionSummary(int Id, string Name, string? Description);
public record RecipientSummary(int Id, string Name, NotificationType Type);
public record QueryExecutionSummary(int Id, DateTime ExecutedAt, double ExecutionTimeMs, NotificationStatus Status, int ResultCount);
public record NotificationSummary(int Id, DateTime SentAt, int ResultCount, string? StoredResults);
public record CommentData(int Id, string Content, string? UserName, DateTime CreatedAt);
public record RelatedTaskSummary(int Id, DateTime CreatedAt, int LatestResultCount, bool Resolved, DateTime? ResolvedAt);
public record ResultCountDataPoint(DateTime Date, int ResultCount);
