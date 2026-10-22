namespace Beacon.Core.Models.Ai;

public record PendingPlanSummary
{
    public int PlanId { get; init; }
    public int ActorId { get; init; }
    public string ActorName { get; init; } = null!;
    public string? UserInstruction { get; init; }
    public string Analysis { get; init; } = null!;
    public int ActionCount { get; init; }
    public DateTime ProposedAt { get; init; }

    /// <summary>When the plan expires (<c>Beacon:AiActors:PlanExpiryDays</c> after it was proposed) unless decided first.</summary>
    public DateTime ExpiresAt { get; init; }
    public int Version { get; init; }
    public int TokensUsed { get; init; }
    public decimal EstimatedCost { get; init; }
}
