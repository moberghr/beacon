using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class RejectAiActorPlanHandler(
    IAiActorService aiActorService,
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

public record RejectAiActorPlanCommand : IRequest<RejectAiActorPlanResult>
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public required string Reason { get; init; }
}

public record RejectAiActorPlanResult
{
    public bool Success { get; init; }
    public int PlanId { get; init; }
}
