using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class RejectAiActorPlanHandler(
    IAiActorServiceExtended aiActorService,
    ILogger<RejectAiActorPlanHandler> logger)
    : IRequestHandler<RejectAiActorPlanCommand, RejectAiActorPlanResult>
{
    public async Task<RejectAiActorPlanResult> Handle(
        RejectAiActorPlanCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Rejecting plan {PlanId} by user {UserId}: {Reason}",
            request.PlanId, request.UserId, request.Reason);

        var options = new RejectPlanOptions
        {
            PlanId = request.PlanId,
            UserId = request.UserId,
            Reason = request.Reason
        };

        await aiActorService.RejectPlanAsync(options, cancellationToken);

        return new RejectAiActorPlanResult
        {
            Success = true,
            PlanId = request.PlanId
        };
    }
}

