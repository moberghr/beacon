using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.AI.Services.Ai.AiActor.Models;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

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

