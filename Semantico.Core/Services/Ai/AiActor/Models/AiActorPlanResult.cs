using Semantico.Core.Data.Enums;

namespace Semantico.Core.Services.Ai.AiActor.Models;

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
/// A proposed action within a plan.
/// </summary>
public record ProposedAction
{
    public AiActorActionType ActionType { get; init; }
    public string Reasoning { get; init; } = null!;
    public Dictionary<string, object?> Parameters { get; init; } = new();

    /// <summary>
    /// For RefineQuery actions, the current SQL
    /// </summary>
    public string? CurrentSql { get; init; }

    /// <summary>
    /// For RefineQuery actions, the proposed new SQL
    /// </summary>
    public string? ProposedSql { get; init; }

    /// <summary>
    /// For RefineQuery actions, whether the query is locked
    /// </summary>
    public bool IsLocked { get; init; }

    /// <summary>
    /// Target query name (for display purposes)
    /// </summary>
    public string? TargetQueryName { get; init; }

    /// <summary>
    /// Target query ID (if applicable)
    /// </summary>
    public int? TargetQueryId { get; init; }
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

/// <summary>
/// Summary of a pending plan for listing.
/// </summary>
public record PendingPlanSummary
{
    public int PlanId { get; init; }
    public int ActorId { get; init; }
    public string ActorName { get; init; } = null!;
    public string? UserInstruction { get; init; }
    public string Analysis { get; init; } = null!;
    public int ActionCount { get; init; }
    public DateTime ProposedAt { get; init; }
    public int Version { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
}
