using MediatR;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class ApproveAiActorPlanHandler(
    IAiActorServiceExtended aiActorService,
    ILogger<ApproveAiActorPlanHandler> logger)
    : IRequestHandler<ApproveAiActorPlanCommand, ApproveAiActorPlanResult>
{
    public async Task<ApproveAiActorPlanResult> Handle(
        ApproveAiActorPlanCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Approving plan {PlanId} by user {UserId}",
            request.PlanId, request.UserId);

        var options = new ApprovePlanOptions
        {
            PlanId = request.PlanId,
            UserId = request.UserId,
            Comment = request.Comment
        };

        var result = await aiActorService.ApprovePlanAsync(options, cancellationToken);

        return new ApproveAiActorPlanResult
        {
            Success = result.Success,
            ExecutionId = result.ExecutionId,
            DecisionSummary = result.DecisionSummary,
            QueriesCreated = result.QueriesCreated,
            QueriesRefined = result.QueriesRefined,
            SubscriptionsCreated = result.SubscriptionsCreated,
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            Duration = result.Duration,
            ErrorMessage = result.ErrorMessage
        };
    }
}

