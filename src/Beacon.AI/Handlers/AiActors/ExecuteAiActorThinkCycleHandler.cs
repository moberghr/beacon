using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class ExecuteAiActorThinkCycleHandler : IRequestHandler<ExecuteAiActorThinkCycleCommand, ExecuteAiActorThinkCycleResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<ExecuteAiActorThinkCycleHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public ExecuteAiActorThinkCycleHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<ExecuteAiActorThinkCycleHandler> logger,
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
        _contextFactory = contextFactory;
    }

    public async Task<ExecuteAiActorThinkCycleResult> Handle(
        ExecuteAiActorThinkCycleCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Manually triggering think cycle for AI Actor {ActorId}", request.ActorId);

        await AiActorOwnership.EnsureCreatorOrAdminAsync(_actorAccessor, _contextFactory, request.ActorId, _logger, cancellationToken);

        var result = await _aiActorService.ExecuteThinkCycleAsync(
            request.ActorId,
            null,
            cancellationToken);

        return new ExecuteAiActorThinkCycleResult
        {
            Success = result.Success,
            ExecutionId = result.ExecutionId,
            Phase = result.Phase,
            DecisionSummary = result.DecisionSummary,
            Findings = result.Findings,
            QueriesAnalyzed = result.QueriesAnalyzed,
            QueriesCreated = result.QueriesCreated,
            QueriesRefined = result.QueriesRefined,
            SubscriptionsCreated = result.SubscriptionsCreated,
            NotificationsTriggered = result.NotificationsTriggered,
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            Duration = result.Duration,
            ErrorMessage = result.ErrorMessage,
            Actions = result.Actions.Select(a => new ExecutedActionInfo
            {
                ActionType = a.ActionType,
                Reasoning = a.Reasoning,
                Success = a.Success,
                ErrorMessage = a.ErrorMessage,
                ResultEntityId = a.ResultEntityId
            }).ToList()
        };
    }
}

