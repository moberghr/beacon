using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class AiConversationHistory : BaseEntity
{
    public int AiAlertConfigurationId { get; set; }
    public int TurnNumber { get; set; }
    public ConversationRole Role { get; set; }
    public string MessageContent { get; set; } = null!;
    public int TokensUsed { get; set; }
    public string Model { get; set; } = null!;
    public DateTime Timestamp { get; set; }
    public string? Metadata { get; set; }

    // Navigation properties
    public AiAlertConfiguration AiAlertConfiguration { get; set; } = null!;
}
