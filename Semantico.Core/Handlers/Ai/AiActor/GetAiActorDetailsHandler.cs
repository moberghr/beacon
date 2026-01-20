using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using Semantico.Core.Services.Ai.AiActor;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class GetAiActorDetailsHandler : IRequestHandler<GetAiActorDetailsQuery, GetAiActorDetailsResult?>
{
    private readonly IAiActorService _aiActorService;
    private readonly ILogger<GetAiActorDetailsHandler> _logger;

    public GetAiActorDetailsHandler(
        IAiActorService aiActorService,
        ILogger<GetAiActorDetailsHandler> logger)
    {
        _aiActorService = aiActorService;
        _logger = logger;
    }

    public async Task<GetAiActorDetailsResult?> Handle(
        GetAiActorDetailsQuery request,
        CancellationToken cancellationToken)
    {
        var actor = await _aiActorService.GetActorAsync(request.ActorId, cancellationToken);
        if (actor == null)
            return null;

        var queries = await _aiActorService.GetActorQueriesAsync(request.ActorId, cancellationToken);
        var subscriptions = await _aiActorService.GetActorSubscriptionsAsync(request.ActorId, cancellationToken);
        var executions = await _aiActorService.GetExecutionHistoryAsync(request.ActorId, request.ExecutionHistoryLimit, cancellationToken);
        var pendingPlans = await _aiActorService.GetPendingPlansAsync(request.ActorId, cancellationToken);

        return new GetAiActorDetailsResult
        {
            ActorId = actor.Id,
            Name = actor.Name,
            Instructions = actor.Instructions,
            AdditionalContext = actor.AdditionalContext,
            DataSourceId = actor.DataSourceId,
            DataSourceName = actor.DataSource?.Name ?? "Unknown",
            Status = actor.Status,
            MaxQueries = actor.MaxQueries,
            MaxSubscriptionsPerQuery = actor.MaxSubscriptionsPerQuery,
            RequiresApproval = actor.RequiresApproval,
            CreatedByUserId = actor.CreatedByUserId,
            TotalTokensUsed = actor.TotalTokensUsed,
            TotalCost = actor.TotalCost,
            LastThinkTime = actor.LastThinkTime,
            ThinkCount = actor.ThinkCount,
            LastError = actor.LastError,
            CreatedTime = actor.CreatedTime,
            ArchivedTime = actor.ArchivedTime,
            PendingPlanCount = pendingPlans.Count,
            Queries = queries.Select(q => new AiActorQueryInfo
            {
                QueryId = q.Id,
                Name = q.Name,
                Description = q.Description,
                SubscriptionCount = q.Subscriptions.Count,
                IsLocked = q.IsLocked,
                LockedAt = q.LockedAt,
                CreatedTime = q.CreatedTime
            }).ToList(),
            Subscriptions = subscriptions.Select(s => new AiActorSubscriptionInfo
            {
                SubscriptionId = s.Id,
                QueryId = s.QueryId,
                QueryName = s.Query?.Name ?? "Unknown",
                CronExpression = s.CronExpression,
                NotificationTrigger = s.NotificationTrigger,
                CreatedTime = s.CreatedTime
            }).ToList(),
            RecentExecutions = executions.Select(e => new AiActorExecutionInfo
            {
                ExecutionId = e.Id,
                Phase = e.Phase,
                StartedAt = e.StartedAt,
                CompletedAt = e.CompletedAt,
                QueriesCreated = e.QueriesCreated,
                SubscriptionsCreated = e.SubscriptionsCreated,
                TokensUsed = e.TokensUsed,
                EstimatedCost = e.EstimatedCost,
                DecisionSummary = e.DecisionSummary,
                ErrorMessage = e.ErrorMessage
            }).ToList()
        };
    }
}

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
