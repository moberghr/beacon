using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Helpers;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetAiActorDetailsHandler : IRequestHandler<GetAiActorDetailsQuery, GetAiActorDetailsResult?>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public GetAiActorDetailsHandler(
        IAiActorServiceExtended aiActorService,
        IDbContextFactory<BeaconContext> contextFactory)
    {
        _aiActorService = aiActorService;
        _contextFactory = contextFactory;
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
        var executions = await _aiActorService.GetExecutionHistoryAsync(request.ActorId, Math.Clamp(request.ExecutionHistoryLimit ?? 10, 1, InputLimits.MaxListTake), cancellationToken);
        var pendingPlans = await _aiActorService.GetPendingPlansAsync(request.ActorId, cancellationToken);
        var ownerDisplayName = actor.CreatedByUserId == null
            ? null
            : await OwnerDisplayNameAsync(actor.CreatedByUserId, cancellationToken);

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
            HasOwner = actor.CreatedByUserId != null,
            OwnerDisplayName = ownerDisplayName,
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

    // The creator is named by display name, or "User {id}" without one: never by user name or external id.
    private async Task<string?> OwnerDisplayNameAsync(string ownerUserId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Users
            .Where(x => x.ExternalId == ownerUserId)
            .Select(UserLabel.OfUser)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
