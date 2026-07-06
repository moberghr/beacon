using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class AiUsageMetrics : ArchivableBaseEntity
{
    public int? UserId { get; set; }
    public int? DataSourceId { get; set; }
    public int? QueryId { get; set; }
    public int? DocumentationId { get; set; }
    public string Provider { get; set; } = null!;
    public string Model { get; set; } = null!;
    public OperationType OperationType { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int TotalTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public bool PromptCacheHit { get; set; }
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public DateTime Timestamp { get; set; }

    // Navigation properties
    public DataSource? DataSource { get; set; }
    public Query? Query { get; set; }
}
