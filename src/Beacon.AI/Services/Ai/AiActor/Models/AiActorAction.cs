using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.Ai.AiActor.Models;

/// <summary>
/// Represents an action planned/executed by an AI Actor during a think cycle
/// </summary>
public class AiActorAction
{
    /// <summary>
    /// Type of action to perform
    /// </summary>
    public AiActorActionType ActionType { get; set; }

    /// <summary>
    /// Reasoning for why this action was chosen
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// Whether the action was successfully executed
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Error message if the action failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// For CREATE_QUERY: The query name
    /// </summary>
    public string? QueryName { get; set; }

    /// <summary>
    /// For CREATE_QUERY/REFINE_QUERY: The SQL query
    /// </summary>
    public string? SqlQuery { get; set; }

    /// <summary>
    /// For REFINE_QUERY/ARCHIVE_QUERY: The target query ID
    /// </summary>
    public int? TargetQueryId { get; set; }

    /// <summary>
    /// For CREATE_SUBSCRIPTION: The cron expression
    /// </summary>
    public string? CronExpression { get; set; }

    /// <summary>
    /// For CREATE_SUBSCRIPTION: The query ID to subscribe to
    /// </summary>
    public int? SubscriptionQueryId { get; set; }

    /// <summary>
    /// For ARCHIVE_SUBSCRIPTION: The subscription ID to archive
    /// </summary>
    public int? TargetSubscriptionId { get; set; }

    /// <summary>
    /// For SEND_NOTIFICATION: The notification message
    /// </summary>
    public string? NotificationMessage { get; set; }

    /// <summary>
    /// For SEND_NOTIFICATION: The severity level
    /// </summary>
    public string? NotificationSeverity { get; set; }

    /// <summary>
    /// ID of the created/modified entity (populated after execution)
    /// </summary>
    public int? ResultEntityId { get; set; }
}

/// <summary>
/// Represents the LLM's planned actions in JSON format
/// </summary>
public class AiActorPlanResponse
{
    /// <summary>
    /// Analysis of the current state
    /// </summary>
    public string? Analysis { get; set; }

    /// <summary>
    /// Notable findings from the analysis
    /// </summary>
    public List<string> Findings { get; set; } = new();

    /// <summary>
    /// List of actions to take
    /// </summary>
    public List<AiActorActionPlan> Actions { get; set; } = new();

    /// <summary>
    /// Whether an urgent notification should be sent
    /// </summary>
    public bool ShouldNotify { get; set; }

    /// <summary>
    /// Summary of why notification is needed (if ShouldNotify is true)
    /// </summary>
    public string? NotificationReason { get; set; }
}

/// <summary>
/// Individual action plan from the LLM
/// </summary>
public class AiActorActionPlan
{
    /// <summary>
    /// Action type: CREATE_QUERY, CREATE_SUBSCRIPTION, REFINE_QUERY, ARCHIVE_QUERY, ARCHIVE_SUBSCRIPTION
    /// </summary>
    public string ActionType { get; set; } = null!;

    /// <summary>
    /// Reasoning for this action
    /// </summary>
    public string? Reasoning { get; set; }

    /// <summary>
    /// Parameters for the action (varies by action type)
    /// </summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();
}
