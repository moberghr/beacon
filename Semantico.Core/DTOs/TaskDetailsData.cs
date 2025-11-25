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
}

public record SubscriptionSummary(int Id, string Name, string? Description);
public record RecipientSummary(int Id, string Name, NotificationType Type);
public record QueryExecutionSummary(int Id, DateTime ExecutedAt, double ExecutionTimeMs, NotificationStatus Status);
public record NotificationSummary(int Id, DateTime SentAt, int ResultCount, string? StoredResults);
