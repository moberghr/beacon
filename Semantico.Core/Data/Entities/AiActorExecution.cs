using Semantico.Core.Data.Entities.Base;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Data.Entities;

/// <summary>
/// Tracks a single "think cycle" execution for an AI Actor
/// </summary>
public class AiActorExecution : BaseEntity
{
    /// <summary>
    /// The actor that performed this execution
    /// </summary>
    public int AiActorId { get; set; }

    /// <summary>
    /// The subscription execution that triggered this think cycle (null if manually triggered)
    /// </summary>
    public int? TriggeringSubscriptionId { get; set; }

    /// <summary>
    /// Current phase of the execution
    /// </summary>
    public AiActorExecutionPhase Phase { get; set; } = AiActorExecutionPhase.Analyzing;

    /// <summary>
    /// When the execution started
    /// </summary>
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When the execution completed (null if still running)
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Number of queries analyzed during this execution
    /// </summary>
    public int QueriesAnalyzed { get; set; }

    /// <summary>
    /// Number of new queries created during this execution
    /// </summary>
    public int QueriesCreated { get; set; }

    /// <summary>
    /// Number of existing queries refined during this execution
    /// </summary>
    public int QueriesRefined { get; set; }

    /// <summary>
    /// Number of subscriptions created during this execution
    /// </summary>
    public int SubscriptionsCreated { get; set; }

    /// <summary>
    /// Number of notifications triggered during this execution
    /// </summary>
    public int NotificationsTriggered { get; set; }

    /// <summary>
    /// Total tokens used during this execution
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Estimated cost of this execution
    /// </summary>
    public decimal EstimatedCost { get; set; }

    /// <summary>
    /// LLM model used for this execution
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// Summary of the actor's analysis and decisions
    /// </summary>
    public string? DecisionSummary { get; set; }

    /// <summary>
    /// JSON serialized list of actions taken during this execution
    /// </summary>
    public string? ActionsJson { get; set; }

    /// <summary>
    /// Error message if the execution failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Full detailed analysis from the LLM in markdown format.
    /// Contains the AI's reasoning, observations, and thought process.
    /// </summary>
    public string? DetailedAnalysis { get; set; }

    /// <summary>
    /// Key findings extracted from the analysis as JSON array.
    /// Example: ["Query X returning empty results", "Table Y has 1000 new rows"]
    /// </summary>
    public string? FindingsJson { get; set; }

    /// <summary>
    /// The plan that led to this execution (if approval workflow was used)
    /// </summary>
    public int? AiActorPlanId { get; set; }

    // Navigation properties
    public AiActor AiActor { get; set; } = null!;
    public Subscription? TriggeringSubscription { get; set; }
    public AiActorPlan? AiActorPlan { get; set; }
}
