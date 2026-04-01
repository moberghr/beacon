namespace Semantico.MCP.Services;

internal interface IQueryExecutionService
{
    Task<QueryExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct);
}

internal record QueryExecutionResult(string? FormattedResult, string? ErrorMessage, int RowCount, bool IsSuccess);
