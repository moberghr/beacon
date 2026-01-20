using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class GenerateAiActorPlanHandler(
    IAiActorService aiActorService,
    ILogger<GenerateAiActorPlanHandler> logger)
    : IRequestHandler<GenerateAiActorPlanCommand, GenerateAiActorPlanResult>
{
    public async Task<GenerateAiActorPlanResult> Handle(
        GenerateAiActorPlanCommand request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Generating plan for AI Actor {ActorId}", request.ActorId);

        var options = new GeneratePlanOptions
        {
            ActorId = request.ActorId,
            UserInstruction = request.UserInstruction,
            TriggeringSubscriptionId = request.TriggeringSubscriptionId
        };

        var result = await aiActorService.GeneratePlanAsync(options, cancellationToken);

        return new GenerateAiActorPlanResult
        {
            Success = result.Success,
            PlanId = result.PlanId,
            Analysis = result.Analysis,
            Findings = result.Findings ?? [],
            ProposedActions = result.ProposedActions ?? [],
            TokensUsed = result.TokensUsed,
            EstimatedCost = result.EstimatedCost,
            ErrorMessage = result.ErrorMessage
        };
    }
}

public record GenerateAiActorPlanCommand : IRequest<GenerateAiActorPlanResult>
{
    public required int ActorId { get; init; }
    public string? UserInstruction { get; init; }
    public int? TriggeringSubscriptionId { get; init; }
}

public record GenerateAiActorPlanResult
{
    public bool Success { get; init; }
    public int? PlanId { get; init; }
    public string? Analysis { get; init; }
    public List<string> Findings { get; init; } = [];
    public List<ProposedAction> ProposedActions { get; init; } = [];
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }
}
