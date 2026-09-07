using Beacon.AI.Services.Mcp;

namespace Beacon.MCP.Services;

/// <summary>
/// Adapts the MCP <see cref="IQueryExecutionService"/> onto the pipeline's <see cref="IAskSqlExecutor"/>
/// seam. Keeps the existing MCP execution path (row formatting + PII masking inside the query executor)
/// intact and deliberately does NOT re-expose raw rows — <c>Rows</c> stays null, the markdown transcript
/// travels in <c>FormattedResult</c>.
/// </summary>
internal sealed class AskSqlExecutor(IQueryExecutionService queryExecutionService) : IAskSqlExecutor
{
    public async Task<AskExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct)
    {
        var result = await queryExecutionService.ExecuteAsync(dataSourceId, sql, maxRows, ct);
        return new AskExecutionResult(result.FormattedResult, result.ErrorMessage, result.RowCount, result.IsSuccess, Structured: result.Structured);
    }

    public async Task<string?> DryRunAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        var outcome = await queryExecutionService.ValidateAsync(dataSourceId, sql, ct);
        return outcome.Skipped ? null : outcome.Error;
    }
}
