using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

internal sealed class QueryExecutionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    ISharedSourceSettings sharedSourceSettings,
    IProjectContext projectContext) : IQueryExecutionService
{
    public async Task<QueryExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var limitedSql = guardrailService.ApplyRowLimit(sql, maxRows, DataSourceSqlDialect.Of(dataSource));
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        // §1.5 backstop — read-only execution path. The database-level guarantee is PostgreSQL and MySQL
        // today (IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement); other engines and
        // non-database providers forward to normal execution and rely on the parser gates upstream.
        var result = await provider.ExecuteReadOnlyQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

        // The tool resolved the project before delegating here (ToolHelper.ResolveProjectId sets ActiveProjectId); 0 falls
        // back to the global effective settings — with the strictest guardrails of the live projects that link the source.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectContext.ActiveProjectId ?? 0, [dataSourceId], ct);

        if (result.Success && result.Rows?.Count > 0)
        {
            // Mask PII column values before returning to the MCP client (§1.6/§1.11). Read-only was
            // already enforced upstream, so here we only need PII detection. Mirrors SemanticSearchService.
            var customPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;
            var sqlPiiColumns = settings.EnablePiiDetection
                ? guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                {
                    ReadOnly = false,
                    DetectPii = true,
                    CustomPiiPatterns = customPatterns,
                    Dialect = DataSourceSqlDialect.Of(dataSource)
                }).PiiColumns ?? []
                : [];

            // The executed SQL resolves the names it aliases PII columns to (email AS contact).
            var rows = PiiRowMasker.Mask(
                guardrailService,
                result.Rows,
                sqlPiiColumns,
                settings.EnablePiiDetection,
                customPatterns,
                limitedSql,
                DataSourceSqlDialect.Of(dataSource));

            var text = $"### Results ({rows.Count} rows)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(rows, maxRows);

            return new QueryExecutionResult(text, null, rows.Count, true, ToolHelper.BuildStructuredPayload(rows, maxRows));
        }

        if (!result.Success)
        {
            // A database error can quote row values: while PII detection is on the caller gets it redacted (a host data
            // source's error is already Beacon's own generic text).
            var error = result.ErrorMessage == null
                ? null
                : DatabaseErrorRedactor.ForCaller(result.ErrorMessage, sql, settings.EnablePiiDetection && dataSource.HostManagedKey == null);

            return new QueryExecutionResult(null, error, 0, false);
        }

        return new QueryExecutionResult("No results returned.\n", null, 0, true);
    }

    public async Task<ProviderDryRunOutcome> ValidateAsync(int dataSourceId, string sql, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        var result = await provider.ValidateQueryAsync(dataSource, sql, ct);

        if (result.IsValid)
        {
            return ProviderDryRunOutcome.Valid();
        }

        var message = result.Errors is { Count: > 0 }
            ? string.Join("; ", result.Errors)
            : "Query validation failed.";

        // Skipped = the engine has no dry-run strategy; the message carries the skip reason so callers
        // can surface it, but it is NOT a validation failure.
        if (result.Skipped || dataSource.HostManagedKey != null)
        {
            return new ProviderDryRunOutcome(message, result.Skipped);
        }

        // A dry-run failure is the database's own error, which can quote row values: redacted while PII detection is on.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectContext.ActiveProjectId ?? 0, [dataSourceId], ct);

        return new ProviderDryRunOutcome(DatabaseErrorRedactor.ForCaller(message, sql, settings.EnablePiiDetection), false);
    }
}
