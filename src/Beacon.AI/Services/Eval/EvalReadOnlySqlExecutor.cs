using Microsoft.EntityFrameworkCore;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.Eval;

/// <summary>
/// The eval harness' <see cref="IAskSqlExecutor"/>: executes SQL strictly read-only through the Core
/// <see cref="IDataSourceProviderFactory"/> (moved verbatim from <c>McpEvalService.ExecuteReadOnlyAsync</c>).
/// Read-only is enforced HERE and never inherited — the shared <see cref="ISqlExecutionGate"/> runs the regex
/// guardrail, the AST validator (fail-closed on parse failure) and the row-limit rewrite, and only then does
/// the provider execute (§1.5). <c>EnforceReadOnly</c> is forced true regardless of the per-project flag so
/// the harness can never mutate a live data source. Returns raw <c>Rows</c> so the caller can fingerprint
/// the result set (R10); <see cref="DryRunAsync"/> returns null because the harness does not dry-run.
/// </summary>
internal sealed class EvalReadOnlySqlExecutor(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    ISqlExecutionGate gate,
    McpSettingsData settings) : IAskSqlExecutor
{
    // Per-statement execution ceiling, matching the MCP query executor.
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(30);

    private DataSource? _dataSource;

    public async Task<AskExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct)
    {
        var result = await ExecuteReadOnlyAsync(dataSourceId, sql, ct);

        return new AskExecutionResult(
            null,
            result.ErrorMessage,
            result.Rows.Count,
            result.Success,
            result.Rows);
    }

    public Task<string?> DryRunAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        // The harness deliberately does not dry-run: a null error keeps the pipeline's dry-run repair
        // trigger dormant, so eval spends its repair budget exactly where production does.
        return Task.FromResult<string?>(null);
    }

    public async Task<ProviderQueryResult> ExecuteReadOnlyAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        var dataSource = await GetDataSourceAsync(dataSourceId, ct);
        var dialect = dataSource.DatabaseEngineType?.ToString();

        // EnforceReadOnly is forced true regardless of the per-project flag — the eval harness must never
        // mutate a live data source. The row cap comes from the same gate evaluation.
        var report = gate.Evaluate(SqlGateRequest.FromSettings(sql, dialect, settings) with
        {
            EnforceReadOnly = true,
            MaxRows = settings.MaxRowLimit
        });

        if (report.Blocked)
        {
            return new ProviderQueryResult { Success = false, ErrorMessage = report.BlockReason };
        }

        var provider = providerFactory.GetProvider(dataSource.DataSourceType);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExecutionTimeout);

        // §1.5 backstop — the read-only execution path (database-level READ ONLY transaction on PostgreSQL;
        // other engines forward to normal execution and rely on the parser gates above).
        return await provider.ExecuteReadOnlyQueryAsync(dataSource, report.FinalSql, new Dictionary<string, object?>(), timeoutCts.Token);
    }

    // The pipeline seam addresses a data source by id; the provider needs the entity. Loaded once and
    // reused for the lifetime of this per-case executor.
    private async Task<DataSource> GetDataSourceAsync(int dataSourceId, CancellationToken ct)
    {
        if (_dataSource != null && _dataSource.Id == dataSourceId)
        {
            return _dataSource;
        }

        await using var context = await contextFactory.CreateDbContextAsync(ct);
        _dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found.");

        return _dataSource;
    }

    /// <summary>
    /// Seeds the already-loaded data source so the harness does not re-read an entity it just fetched.
    /// </summary>
    public void UseDataSource(DataSource dataSource)
    {
        _dataSource = dataSource;
    }
}
