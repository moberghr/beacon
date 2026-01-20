namespace Semantico.Core.DTOs;

public record TaskData
{
    public required int Id { get; init; }
    public required string SubscriptionName { get; init; }
    public required string QueryName { get; init; }
    public required int LatestResultCount { get; init; }
    public DateTime? LastNotificationAt { get; init; }
    public required int NotificationCount { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required bool Resolved { get; init; }
    public DateTime? ResolvedAt { get; init; }
    public string? ResolvedByUserName { get; init; }

    /// <summary>
    /// Total number of query executions for this subscription since task creation
    /// </summary>
    public int ExecutionCount { get; init; }

    /// <summary>
    /// Number of distinct result counts seen (indicates result volatility)
    /// </summary>
    public int UniqueResultCounts { get; init; }

    /// <summary>
    /// AI Actor ID if the subscription is managed by an AI Actor, null if user-created
    /// </summary>
    public int? AiActorId { get; init; }

    /// <summary>
    /// Name of the AI Actor managing the subscription
    /// </summary>
    public string? AiActorName { get; init; }
}
