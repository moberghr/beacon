using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class RefineAiActorHandler : IRequestHandler<RefineAiActorCommand, RefineAiActorResult>
{
    private readonly IAiActorServiceExtended _aiActorService;
    private readonly ILogger<RefineAiActorHandler> _logger;
    private readonly IBeaconActorAccessor _actorAccessor;
    private readonly IDbContextFactory<BeaconContext> _contextFactory;

    public RefineAiActorHandler(
        IAiActorServiceExtended aiActorService,
        ILogger<RefineAiActorHandler> logger,
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory)
    {
        _aiActorService = aiActorService;
        _logger = logger;
        _actorAccessor = actorAccessor;
        _contextFactory = contextFactory;
    }

    public async Task<RefineAiActorResult> Handle(
        RefineAiActorCommand request,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation("Refining AI Actor {ActorId} with user feedback", request.ActorId);

        await AiActorOwnership.EnsureCreatorOrAdminAsync(_actorAccessor, _contextFactory, request.ActorId, _logger, cancellationToken);

        var result = await _aiActorService.RefineActorAsync(
            request.ActorId,
            request.Feedback,
            cancellationToken);

        return new RefineAiActorResult
        {
            Success = result.Success,
            ExecutionId = result.ExecutionId,
            Phase = result.Phase,
            DecisionSummary = result.DecisionSummary,
            QueriesCreated = result.QueriesCreated,
            QueriesRefined = result.QueriesRefined,
            SubscriptionsCreated = result.SubscriptionsCreated,
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            ErrorMessage = result.ErrorMessage
        };
    }
}

