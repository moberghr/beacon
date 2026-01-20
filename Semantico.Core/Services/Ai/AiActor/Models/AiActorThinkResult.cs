using Semantico.Core.Data.Enums;

namespace Semantico.Core.Services.Ai.AiActor.Models;

/// <summary>
/// Result of an AI Actor think cycle
/// </summary>
public class AiActorThinkResult
{
    /// <summary>
    /// Whether the think cycle completed successfully
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// ID of the execution record
    /// </summary>
    public int ExecutionId { get; set; }

    /// <summary>
    /// Final phase of the execution
    /// </summary>
    public AiActorExecutionPhase Phase { get; set; }

    /// <summary>
    /// Summary of the actor's analysis and decisions
    /// </summary>
    public string? DecisionSummary { get; set; }

    /// <summary>
    /// Notable findings from the analysis
    /// </summary>
    public List<string> Findings { get; set; } = new();

    /// <summary>
    /// Actions executed during this cycle
    /// </summary>
    public List<AiActorAction> Actions { get; set; } = new();

    /// <summary>
    /// Number of queries analyzed
    /// </summary>
    public int QueriesAnalyzed { get; set; }

    /// <summary>
    /// Number of new queries created
    /// </summary>
    public int QueriesCreated { get; set; }

    /// <summary>
    /// Number of queries refined
    /// </summary>
    public int QueriesRefined { get; set; }

    /// <summary>
    /// Number of subscriptions created
    /// </summary>
    public int SubscriptionsCreated { get; set; }

    /// <summary>
    /// Number of notifications triggered
    /// </summary>
    public int NotificationsTriggered { get; set; }

    /// <summary>
    /// Total tokens used
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Estimated cost
    /// </summary>
    public decimal EstimatedCost { get; set; }

    /// <summary>
    /// Error message if the cycle failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Duration of the think cycle
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// Creates a success result
    /// </summary>
    public static AiActorThinkResult CreateSuccess(int executionId, string? summary = null) => new()
    {
        Success = true,
        ExecutionId = executionId,
        Phase = AiActorExecutionPhase.Completed,
        DecisionSummary = summary
    };

    /// <summary>
    /// Creates a failure result
    /// </summary>
    public static AiActorThinkResult CreateFailure(int executionId, string errorMessage) => new()
    {
        Success = false,
        ExecutionId = executionId,
        Phase = AiActorExecutionPhase.Failed,
        ErrorMessage = errorMessage
    };
}
