using System.Text.Json.Nodes;

namespace Beacon.AI.Services.Mcp;

/// <summary>
/// Execution seam for <see cref="IAskSqlPipeline"/>. Keeps the pipeline in <c>Beacon.AI</c> free of any
/// <c>Beacon.MCP</c> type (§2.4): the MCP adapter wraps <c>IQueryExecutionService</c> (markdown result,
/// no raw rows) and the eval executor runs through the Core provider factory (raw rows, read-only forced).
/// </summary>
public interface IAskSqlExecutor
{
    Task<AskExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct);

    /// <summary>
    /// Dry-run validation through the provider (e.g. EXPLAIN / sp_describe_first_result_set) without
    /// executing the query. Returns null when valid — or when the executor does not dry-run at all —
    /// and the error text otherwise.
    /// </summary>
    Task<string?> DryRunAsync(int dataSourceId, string sql, CancellationToken ct);
}

/// <summary>
/// One execution of a candidate query. <paramref name="FormattedResult"/> is the rendered markdown the
/// <c>ask</c> transcript embeds (null when the executor does not format); <paramref name="Rows"/> is the
/// raw result set, populated only by executors that expose it (the eval harness fingerprints it, R10).
/// </summary>
public sealed record AskExecutionResult(
    string? FormattedResult,
    string? ErrorMessage,
    int RowCount,
    bool IsSuccess,
    IReadOnlyList<Dictionary<string, object?>>? Rows = null,
    JsonNode? Structured = null);
