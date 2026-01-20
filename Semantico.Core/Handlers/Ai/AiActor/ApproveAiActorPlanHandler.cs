using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class ApproveAiActorPlanHandler(
    IAiActorService aiActorService,
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

public record ApproveAiActorPlanCommand : IRequest<ApproveAiActorPlanResult>
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public string? Comment { get; init; }
}

public record ApproveAiActorPlanResult
{
    public bool Success { get; init; }
    public int? ExecutionId { get; init; }
    public string? DecisionSummary { get; init; }
    public int QueriesCreated { get; init; }
    public int QueriesRefined { get; init; }
    public int SubscriptionsCreated { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public TimeSpan? Duration { get; init; }
    public string? ErrorMessage { get; init; }
}
