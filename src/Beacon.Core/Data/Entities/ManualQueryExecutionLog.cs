using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// The execution log of the statements Beacon runs on a data source for its own features: every statement the
/// <c>GatedSqlRunner</c> runs (query previews and steps, subscription runs, AI actor checks, migration reads, column value
/// samples) and every documentation enum discovery statement, written as <see cref="QueryExecutionLogStatus.Started"/>
/// before the statement runs and closed when it ends; plus one summary row per query preview run.
/// </summary>
public class ManualQueryExecutionLog : BaseEntity
{
    /// <summary>
    /// The caller's user id as a preview's summary row recorded it. Null on statement rows, which record
    /// <see cref="TriggeredByUserId"/> instead.
    /// </summary>
    public string? UserId { get; set; }

    /// <summary>
    /// The <c>Users.Id</c> of the user the statement ran for: the request's caller, or the owner scheduled work runs as.
    /// Null for work the system does on its own, and for rows recorded before Beacon kept it.
    /// </summary>
    public int? TriggeredByUserId { get; set; }

    /// <summary>
    /// Where the statement is: <see cref="QueryExecutionLogStatus.Started"/> while it runs (a row that stays Started is a
    /// statement whose end was never recorded), then how it ended. Null for rows recorded only after the statement ran.
    /// </summary>
    public QueryExecutionLogStatus? Status { get; set; }

    /// <summary>When the statement's end was recorded; null while it is <see cref="QueryExecutionLogStatus.Started"/>.</summary>
    public DateTime? CompletedTime { get; set; }

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
    /// What ran it: one of <see cref="Beacon.Core.Services.SqlExecutionContexts"/>, or "FullQueryPreview" on a summary row
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
