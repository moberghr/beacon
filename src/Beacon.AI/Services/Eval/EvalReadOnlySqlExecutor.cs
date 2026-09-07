using Microsoft.EntityFrameworkCore;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;

namespace Beacon.AI.Services.Eval;

/// <summary>
/// The eval harness' <see cref="IAskSqlExecutor"/>: executes SQL strictly read-only through the Core
/// <see cref="IDataSourceProviderFactory"/> (moved verbatim from <c>McpEvalService.ExecuteReadOnlyAsync</c>).
/// Read-only is enforced HERE and never inherited — the regex guardrail runs first, then the AST validator
/// (fail-closed on parse failure), then the row limit, and only then does the provider execute (§1.5).
/// <c>ReadOnly</c> is forced true regardless of the per-project flag so the harness can never mutate a
/// live data source. Returns raw <c>Rows</c> so the caller can fingerprint the result set (R10);
/// <see cref="DryRunAsync"/> returns null because the harness does not dry-run.
/// </summary>
internal sealed class EvalReadOnlySqlExecutor(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    SqlReadOnlyAstValidator readOnlyAstValidator,
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

        // ReadOnly is forced true regardless of the per-project EnforceReadOnly flag — the eval harness
        // must never mutate a live data source.
        var guardrail = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
        {
            ReadOnly = true,
            DetectPii = settings.EnablePiiDetection,
            CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
        });

        if (!guardrail.IsValid)
        {
            return new ProviderQueryResult { Success = false, ErrorMessage = guardrail.Error };
        }

        var astError = readOnlyAstValidator.Validate(sql, dialect);
        if (astError != null)
        {
            return new ProviderQueryResult { Success = false, ErrorMessage = astError };
        }

        var limitedSql = guardrailService.ApplyRowLimit(sql, settings.MaxRowLimit, dataSource.DatabaseEngineType?.ToString());
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(ExecutionTimeout);

        return await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);
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
