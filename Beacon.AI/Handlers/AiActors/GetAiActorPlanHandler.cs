using System.Text.Json;
using MediatR;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetAiActorPlanHandler(IAiActorServiceExtended aiActorService)
    : IRequestHandler<GetAiActorPlanQuery, GetAiActorPlanResult?>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<GetAiActorPlanResult?> Handle(
        GetAiActorPlanQuery request,
        CancellationToken cancellationToken)
    {
        var plan = await aiActorService.GetPlanAsync(request.PlanId, cancellationToken);

        if (plan == null)
            return null;

        var proposedActions = !string.IsNullOrEmpty(plan.ActionsJson)
            ? JsonSerializer.Deserialize<List<ProposedAction>>(plan.ActionsJson, JsonOptions) ?? []
            : [];

        var findings = !string.IsNullOrEmpty(plan.FindingsJson)
            ? JsonSerializer.Deserialize<List<string>>(plan.FindingsJson, JsonOptions) ?? []
            : [];

        return new GetAiActorPlanResult
        {
            PlanId = plan.Id,
            ActorId = plan.AiActorId,
            ActorName = plan.AiActor.Name,
            Status = plan.Status,
            UserInstruction = plan.UserInstruction,
            Analysis = plan.Analysis,
            Findings = findings,
            ProposedActions = proposedActions,
            ProposedAt = plan.ProposedAt,
            ReviewedAt = plan.ReviewedAt,
            ReviewedByUserId = plan.ReviewedByUserId,
            ReviewerComment = plan.ReviewerComment,
            ExecutedAt = plan.ExecutedAt,
            ExecutionId = plan.AiActorExecutionId,
            TokensUsed = plan.TokensUsed,
            EstimatedCost = plan.EstimatedCost,
            Model = plan.Model,
            Version = plan.Version,
            ParentPlanId = plan.ParentPlanId
        };
    }
}

