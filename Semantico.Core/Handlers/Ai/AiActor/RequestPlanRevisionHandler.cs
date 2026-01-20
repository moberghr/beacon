using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class RequestPlanRevisionHandler(
    IAiActorService aiActorService,
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

public record RequestPlanRevisionCommand : IRequest<RequestPlanRevisionResult>
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public required string Feedback { get; init; }
}

public record RequestPlanRevisionResult
{
    public bool Success { get; init; }
    public int OriginalPlanId { get; init; }
    public int? NewPlanId { get; init; }
    public string? Analysis { get; init; }
    public List<string> Findings { get; init; } = [];
    public List<ProposedAction> ProposedActions { get; init; } = [];
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }
}
