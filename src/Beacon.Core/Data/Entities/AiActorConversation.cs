using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Multi-turn conversation history for an AI Actor, used for refinement and context
/// </summary>
public class AiActorConversation : BaseEntity
{
    /// <summary>
    /// The actor this conversation belongs to
    /// </summary>
    public int AiActorId { get; set; }

    /// <summary>
    /// Optional link to a specific execution that generated this message
    /// </summary>
    public int? AiActorExecutionId { get; set; }

    /// <summary>
    /// Turn number in the conversation (for ordering)
    /// </summary>
    public int TurnNumber { get; set; }

    /// <summary>
    /// Role of the message sender (User, Assistant, System)
    /// </summary>
    public ConversationRole Role { get; set; }

    /// <summary>
    /// Content of the message
    /// </summary>
    public string MessageContent { get; set; } = null!;

    /// <summary>
    /// Tokens used for this message (for assistant messages)
    /// </summary>
    public int TokensUsed { get; set; }

    /// <summary>
    /// Model used for this message (for assistant messages)
    /// </summary>
    public string? Model { get; set; }

    /// <summary>
    /// When this message was created
    /// </summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Optional JSON metadata for this message
    /// </summary>
    public string? Metadata { get; set; }

    // Navigation properties
    public AiActor AiActor { get; set; } = null!;
    public AiActorExecution? AiActorExecution { get; set; }
}
