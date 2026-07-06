using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class AiAlertConfiguration : ArchivableBaseEntity
{
    public int DataSourceId { get; set; }
    public string Name { get; set; } = null!;
    public string NaturalLanguageDescription { get; set; } = null!;
    public string GeneratedSql { get; set; } = null!;
    public string? FinalSql { get; set; }
    public string GeneratedByModel { get; set; } = null!;
    public string? GenerationReasoning { get; set; }
    public decimal? ConfidenceScore { get; set; }
    public AlertStatus Status { get; set; }
    public string? ValidationErrors { get; set; }
    public string? UserFeedback { get; set; }
    public int? SubscriptionId { get; set; }
    public int ConversationTurns { get; set; }
    public int TokensUsed { get; set; }
    public decimal EstimatedCost { get; set; }
    public string CreatedBy { get; set; } = null!;
    public string ModifiedBy { get; set; } = null!;

    // Navigation properties
    public DataSource DataSource { get; set; } = null!;
    public Subscription? Subscription { get; set; }
    public ICollection<AiConversationHistory> ConversationHistory { get; set; } = new List<AiConversationHistory>();
}
