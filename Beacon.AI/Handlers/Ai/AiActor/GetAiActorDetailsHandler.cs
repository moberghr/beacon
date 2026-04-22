using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Handlers.Ai.AiActor;

namespace Beacon.AI.Handlers.Ai.AiActor;

internal sealed class GetAiActorDetailsHandler : IRequestHandler<GetAiActorDetailsQuery, GetAiActorDetailsResult?>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<GetAiActorDetailsHandler> _logger;

    public GetAiActorDetailsHandler(
        IAiActorServiceExtended aiActorService,
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

