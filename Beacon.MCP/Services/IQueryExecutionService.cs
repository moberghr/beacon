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

internal record QueryExecutionResult(string? FormattedResult, string? ErrorMessage, int RowCount, bool IsSuccess);
