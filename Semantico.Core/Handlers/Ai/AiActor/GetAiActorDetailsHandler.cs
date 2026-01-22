using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Ai;
using Semantico.Core.Data.Entities;



namespace Semantico.Core.Handlers.Ai.AiActor;

public record GetAiActorDetailsQuery : IRequest<GetAiActorDetailsResult?>
{
    public required int ActorId { get; init; }
    public int? ExecutionHistoryLimit { get; init; } = 10;
}

public record GetAiActorDetailsResult
{
    public int ActorId { get; init; }
    public string Name { get; init; } = null!;
    public string Instructions { get; init; } = null!;
    public string? AdditionalContext { get; init; }
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public AiActorStatus Status { get; init; }
    public int MaxQueries { get; init; }
    public int MaxSubscriptionsPerQuery { get; init; }
    public bool RequiresApproval { get; init; }
    public string? CreatedByUserId { get; init; }
    public int TotalTokensUsed { get; init; }
    public decimal TotalCost { get; init; }
    public DateTime? LastThinkTime { get; init; }
    public int ThinkCount { get; init; }
    public string? LastError { get; init; }
    public DateTime CreatedTime { get; init; }
    public DateTime? ArchivedTime { get; init; }
    public int PendingPlanCount { get; init; }
    public List<AiActorQueryInfo> Queries { get; init; } = new();
    public List<AiActorSubscriptionInfo> Subscriptions { get; init; } = new();
    public List<AiActorExecutionInfo> RecentExecutions { get; init; } = new();
}

public record AiActorQueryInfo
{
    public int QueryId { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public int SubscriptionCount { get; init; }
    public bool IsLocked { get; init; }
    public DateTime? LockedAt { get; init; }
    public DateTime CreatedTime { get; init; }
}

public record AiActorSubscriptionInfo
{
    public int SubscriptionId { get; init; }
    public int QueryId { get; init; }
    public string QueryName { get; init; } = null!;
    public string CronExpression { get; init; } = null!;
    public NotificationTrigger NotificationTrigger { get; init; }
    public DateTime CreatedTime { get; init; }
}

public record AiActorExecutionInfo
{
    public int ExecutionId { get; init; }
    public AiActorExecutionPhase Phase { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public int QueriesCreated { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? DecisionSummary { get; init; }
    public string? ErrorMessage { get; init; }
}
