using System.Text.Json.Nodes;

namespace Beacon.MCP.Services;

internal interface IQueryExecutionService
{
    Task<QueryExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct);

    /// <summary>
    /// Dry-run validation through the provider (e.g. EXPLAIN / sp_describe_first_result_set)
    /// without executing the query. Valid = null Error and not Skipped; Skipped = the engine has no
    /// dry-run strategy, so NOTHING was checked (never a pass, never a repairable failure).
    /// </summary>
    Task<ProviderDryRunOutcome> ValidateAsync(int dataSourceId, string sql, CancellationToken ct);
}

/// <summary>
/// Outcome of the provider dry-run. <see cref="Error"/> carries the provider's message — a validation
/// failure when <see cref="Skipped"/> is false, the skip reason when it is true. <see cref="IsValid"/>
/// is true only when the provider actually checked the query and found nothing wrong.
/// </summary>
internal readonly record struct ProviderDryRunOutcome(string? Error, bool Skipped)
{
    public bool IsValid => Error == null && !Skipped;

    public static ProviderDryRunOutcome Valid() => new(null, false);
}

// Structured carries the machine-readable { columns, rows, row_count, truncated } payload built from the
// same (PII-masked) rows as FormattedResult; null on failure, on empty results, and in tests that only
// exercise the markdown path.
internal record QueryExecutionResult(string? FormattedResult, string? ErrorMessage, int RowCount, bool IsSuccess, JsonNode? Structured = null);
