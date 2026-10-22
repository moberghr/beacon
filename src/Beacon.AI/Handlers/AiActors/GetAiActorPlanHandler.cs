using System.Text.Json;
using MediatR;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Enums;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Handlers.AiActors;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetAiActorPlanHandler(
    IAiActorServiceExtended aiActorService,
    IOptionsMonitor<AiActorOptions> options,
    TimeProvider timeProvider)
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

        var expiresAt = options.CurrentValue.PlanExpiresAt(plan.ProposedAt);

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
            ExpiresAt = expiresAt,
            IsExpired = plan.Status == AiActorPlanStatus.PendingApproval && timeProvider.GetUtcNow().UtcDateTime >= expiresAt,
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

