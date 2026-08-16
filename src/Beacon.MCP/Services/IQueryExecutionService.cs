using System.Text.Json.Nodes;

namespace Beacon.MCP.Services;

internal interface IQueryExecutionService
{
    Task<QueryExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct);

    /// <summary>
    /// Dry-run validation through the provider (e.g. EXPLAIN / sp_describe_first_result_set)
    /// without executing the query. Returns null when valid, the error text otherwise.
    /// </summary>
    Task<string?> ValidateAsync(int dataSourceId, string sql, CancellationToken ct);
}

// Structured carries the machine-readable { columns, rows, row_count, truncated } payload built from the
// same (PII-masked) rows as FormattedResult; null on failure, on empty results, and in tests that only
// exercise the markdown path.
internal record QueryExecutionResult(string? FormattedResult, string? ErrorMessage, int RowCount, bool IsSuccess, JsonNode? Structured = null);
