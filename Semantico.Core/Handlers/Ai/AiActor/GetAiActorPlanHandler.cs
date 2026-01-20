using System.Text.Json;
using MediatR;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services.Ai.AiActor;
using Semantico.Core.Services.Ai.AiActor.Models;

namespace Semantico.Core.Handlers.Ai.AiActor;

internal sealed class GetAiActorPlanHandler(IAiActorService aiActorService)
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

public record GetAiActorPlanQuery : IRequest<GetAiActorPlanResult?>
{
    public required int PlanId { get; init; }
}

public record GetAiActorPlanResult
{
    public int PlanId { get; init; }
    public int ActorId { get; init; }
    public string ActorName { get; init; } = null!;
    public AiActorPlanStatus Status { get; init; }
    public string? UserInstruction { get; init; }
    public string Analysis { get; init; } = null!;
    public List<string> Findings { get; init; } = [];
    public List<ProposedAction> ProposedActions { get; init; } = [];
    public DateTime ProposedAt { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public string? ReviewedByUserId { get; init; }
    public string? ReviewerComment { get; init; }
    public DateTime? ExecutedAt { get; init; }
    public int? ExecutionId { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? Model { get; init; }
    public int Version { get; init; }
    public int? ParentPlanId { get; init; }
}
