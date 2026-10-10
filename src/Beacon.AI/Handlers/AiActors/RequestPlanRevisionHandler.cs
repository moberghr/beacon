using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class RequestPlanRevisionHandler(
    IAiActorServiceExtended aiActorService,
    ILogger<RequestPlanRevisionHandler> logger,
    IBeaconActorAccessor actorAccessor,
    IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<RequestPlanRevisionCommand, RequestPlanRevisionResult>
{
    public async Task<RequestPlanRevisionResult> Handle(
        RequestPlanRevisionCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Requesting revision for plan {PlanId} by user {UserId}",
            request.PlanId, request.UserId);

        // A revision runs the LLM again with the caller's feedback: the actor's creator's or an Admin's to ask for.
        await AiActorOwnership.EnsureCreatorOrAdminOfPlanAsync(actorAccessor, contextFactory, request.PlanId, logger, cancellationToken);

        var options = new RequestRevisionOptions
        {
            PlanId = request.PlanId,
            UserId = request.UserId,
            Feedback = request.Feedback
        };

        var result = await aiActorService.RequestPlanRevisionAsync(options, cancellationToken);

        return new RequestPlanRevisionResult
        {
            Success = result.Success,
            OriginalPlanId = request.PlanId,
            NewPlanId = result.PlanId,
            Analysis = result.Analysis,
            Findings = result.Findings ?? [],
            ProposedActions = result.ProposedActions ?? [],
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            ErrorMessage = result.ErrorMessage
        };
    }
}

