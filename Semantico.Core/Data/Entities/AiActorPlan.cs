using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Represents a proposed plan from an AI Actor that requires user approval before execution.
/// </summary>
public class AiActorPlan : BaseEntity
{
    /// <summary>
    /// The AI Actor that created this plan
    /// </summary>
    public int AiActorId { get; set; }

    /// <summary>
    /// The execution created when this plan was approved and executed
    /// </summary>
    public int? AiActorExecutionId { get; set; }

    /// <summary>
    /// Current status of this plan
    /// </summary>
    public AiActorPlanStatus Status { get; set; } = AiActorPlanStatus.PendingApproval;

    /// <summary>
    /// User's original instruction that triggered this plan (e.g., "monitor for failed payments")
    /// </summary>
    public string? UserInstruction { get; set; }

    /// <summary>
    /// AI's analysis of the current state and what it observed (markdown format)
    /// </summary>
    public string Analysis { get; set; } = null!;

    /// <summary>
    /// Key findings from the analysis as JSON array
    /// Example: ["Found 3 tables related to payments", "Current query returns 0 results", ...]
    /// </summary>
    public string? FindingsJson { get; set; }

    /// <summary>
    /// Proposed actions as JSON array of objects
    /// Example: [{"action": "refine_query", "queryId": 1, "reasoning": "...", "proposedSql": "..."}]
    /// </summary>
    public string ActionsJson { get; set; } = null!;

    /// <summary>
    /// When this plan was proposed
    /// </summary>
    public DateTime ProposedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the plan was reviewed (approved/rejected/revision requested)
    /// </summary>
    public DateTime? ReviewedAt { get; set; }

    /// <summary>
    /// User ID of who reviewed the plan
    /// </summary>
    public string? ReviewedByUserId { get; set; }

    /// <summary>
    /// Comment from the reviewer (especially for rejection or revision requests)
    /// </summary>
    public string? ReviewerComment { get; set; }

    /// <summary>
    /// When the plan was executed (after approval)
    /// </summary>
    public DateTime? ExecutedAt { get; set; }

    /// <summary>
    /// Number of LLM tokens used to generate this plan
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Estimated cost of generating this plan
    /// </summary>
    public decimal EstimatedCost { get; set; }

    /// <summary>
    /// LLM model used to generate this plan
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Revision number (1 = original, 2+ = revisions based on feedback)
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Link to the original plan if this is a revision
    /// </summary>
    public int? ParentPlanId { get; set; }

    // Navigation properties
    public AiActor AiActor { get; set; } = null!;
    public AiActorExecution? AiActorExecution { get; set; }
    public AiActorPlan? ParentPlan { get; set; }
    public List<AiActorPlan> Revisions { get; set; } = new();
    public List<QueryStepChangeHistory> ChangeHistory { get; set; } = new();
}
