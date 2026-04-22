namespace Beacon.AI.Services.Ai.AiActor.Models;

/// <summary>
/// Options for creating a new AI Actor
/// </summary>
public class CreateAiActorOptions
{
    /// <summary>
    /// Display name for the actor
    /// </summary>
    public required string Name { get; set; }

    /// <summary>
    /// High-level instructions for what the actor should monitor
    /// </summary>
    public required string Instructions { get; set; }

    /// <summary>
    /// The data source to monitor
    /// </summary>
    public required int DataSourceId { get; set; }

    /// <summary>
    /// Additional context about the data source or business rules
    /// </summary>
    public string? AdditionalContext { get; set; }

    /// <summary>
    /// Maximum number of queries the actor can create
    /// </summary>
    public int MaxQueries { get; set; } = 10;

    /// <summary>
    /// Maximum subscriptions per query
    /// </summary>
    public int MaxSubscriptionsPerQuery { get; set; } = 3;

    /// <summary>
    /// User ID who is creating this actor
    /// </summary>
    public string? CreatedByUserId { get; set; }

    /// <summary>
    /// Default recipients for notifications (will be assigned to created subscriptions)
    /// </summary>
    public List<int>? DefaultRecipientIds { get; set; }

    /// <summary>
    /// Whether to activate the actor immediately after creation
    /// </summary>
    public bool ActivateImmediately { get; set; } = true;
}
