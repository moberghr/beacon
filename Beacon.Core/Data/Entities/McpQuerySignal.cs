using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class McpQuerySignal : BaseEntity
{
    public int? ProjectId { get; set; }
    public int? DataSourceId { get; set; }
    public int? UserId { get; set; }

    public required string Tool { get; set; }
    public required string Question { get; set; }
    public string? IntentClassification { get; set; }
    public string? RoutingDecision { get; set; }

    public string? GeneratedSql { get; set; }
    public string? TablesUsed { get; set; }
    public string? ColumnsUsed { get; set; }

    public bool SchemaValidationFailed { get; set; }
    public string? SchemaValidationError { get; set; }

    public bool ExecutionFailed { get; set; }
    public string? ExecutionError { get; set; }

    public bool RetryAttempted { get; set; }
    public bool RetrySucceeded { get; set; }
    public string? CorrectedSql { get; set; }

    public int? ResultRowCount { get; set; }
    public int ExecutionTimeMs { get; set; }
    public bool IsSuccessful { get; set; }
}
