using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.Ai.AiActor;

namespace Beacon.AI.Handlers.Ai.AiActor;

internal sealed class RequestPlanRevisionHandler(
    IAiActorServiceExtended aiActorService,
    ILogger<RequestPlanRevisionHandler> logger)
    : IRequestHandler<RequestPlanRevisionCommand, RequestPlanRevisionResult>
{
    public async Task<RequestPlanRevisionResult> Handle(
        RequestPlanRevisionCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Requesting revision for plan {PlanId} by user {UserId}",
            request.PlanId, request.UserId);

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

