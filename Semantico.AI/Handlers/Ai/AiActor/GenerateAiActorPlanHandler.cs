using MediatR;
using Microsoft.Extensions.Logging;
using Semantico.AI.Services.Ai.AiActor;
using Semantico.AI.Services.Ai.AiActor.Models;
using Semantico.Core.Handlers.Ai.AiActor;

namespace Semantico.AI.Handlers.Ai.AiActor;

internal sealed class GenerateAiActorPlanHandler(
    IAiActorServiceExtended aiActorService,
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

