using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.Ai.AiActor.Models;

/// <summary>
/// Result of generating an AI Actor plan.
/// </summary>
public record AiActorPlanResult
{
    public bool Success { get; init; }
    public int? PlanId { get; init; }
    public string? Analysis { get; init; }
    public List<string>? Findings { get; init; }
    public List<ProposedAction>? ProposedActions { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
    public string? ErrorMessage { get; init; }

    public static AiActorPlanResult CreateSuccess(
        int planId,
        string analysis,
        List<string>? findings,
        List<ProposedAction> proposedActions,
        int tokensUsed,
        decimal estimatedCost)
    {
        return new AiActorPlanResult
        {
            Success = true,
            PlanId = planId,
            Analysis = analysis,
            Findings = findings,
            ProposedActions = proposedActions,
            TokensUsed = tokensUsed,
            EstimatedCost = estimatedCost
        };
    }

    public static AiActorPlanResult CreateFailure(string errorMessage)
    {
        return new AiActorPlanResult
        {
            Success = false,
            ErrorMessage = errorMessage
        };
    }
}

/// <summary>
/// Options for generating a plan.
/// </summary>
public record GeneratePlanOptions
{
    public required int ActorId { get; init; }
    public string? UserInstruction { get; init; }
    public int? TriggeringSubscriptionId { get; init; }
    public int? ParentPlanId { get; init; }
    public string? PreviousFeedback { get; init; }
}

/// <summary>
/// Options for approving a plan.
/// </summary>
public record ApprovePlanOptions
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public string? Comment { get; init; }
}

/// <summary>
/// Options for rejecting a plan.
/// </summary>
public record RejectPlanOptions
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public required string Reason { get; init; }
}

/// <summary>
/// Options for requesting a plan revision.
/// </summary>
public record RequestRevisionOptions
{
    public required int PlanId { get; init; }
    public string? UserId { get; init; }
    public required string Feedback { get; init; }
}

