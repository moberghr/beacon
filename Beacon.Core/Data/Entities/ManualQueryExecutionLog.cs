using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// Tracks all manual query executions initiated by users in the UI.
/// This includes queries run in data source editors and query test/preview operations.
/// </summary>
public class ManualQueryExecutionLog : BaseEntity
{
    /// <summary>
    /// User ID who executed the query (nullable until middleware is implemented)
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// The SQL query or command text that was executed
    /// </summary>
    public required string QueryText { get; set; }

    /// <summary>
    /// Number of rows/records returned by the query
    /// </summary>
    public required int ResultCount { get; set; }

    /// <summary>
    /// Query execution duration in milliseconds
    /// </summary>
    public required double ExecutionTimeMs { get; set; }

    /// <summary>
    /// Data source where the query was executed (nullable for multi-step queries)
    /// </summary>
    public int? DataSourceId { get; set; }

    /// <summary>
    /// Optional context: "DataSourceEditor", "QueryStepPreview", "FullQueryPreview"
    /// </summary>
    public string? ExecutionContext { get; set; }

    /// <summary>
    /// Whether the query execution was successful
    /// </summary>
    public required bool Success { get; set; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    public string? ErrorMessage { get; set; }

    // Navigation properties
    public DataSource? DataSource { get; set; }
}
