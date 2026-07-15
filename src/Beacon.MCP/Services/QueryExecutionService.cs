using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Tools;

namespace Beacon.MCP.Services;

internal sealed class QueryExecutionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider) : IQueryExecutionService
{
    public async Task<QueryExecutionResult> ExecuteAsync(int dataSourceId, string sql, int maxRows, CancellationToken ct)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException($"Data source {dataSourceId} not found");

        var limitedSql = guardrailService.ApplyRowLimit(sql, maxRows, dataSource.DatabaseEngineType?.ToString());
        var provider = providerFactory.GetProvider(dataSource.DataSourceType);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
        var result = await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

        if (result.Success && result.Rows?.Count > 0)
        {
            // Mask PII column values before returning to the MCP client (§1.6/§1.11). Read-only was
            // already enforced upstream, so here we only need PII detection. Mirrors SemanticSearchService.
            var rows = result.Rows;
            var settings = await settingsProvider.GetSettingsAsync(ct);
            if (settings.EnablePiiDetection)
            {
                var piiColumns = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                {
                    ReadOnly = false,
                    DetectPii = true,
                    CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                }).PiiColumns;

                if (piiColumns is { Count: > 0 } piiCols)
                {
                    rows = rows.Select(x => guardrailService.MaskPiiValues(x, piiCols)).ToList();
                }
            }

            var text = $"### Results ({rows.Count} rows)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(rows);

            return new QueryExecutionResult(text, null, rows.Count, true);
        }

        if (!result.Success)
        {
            return new QueryExecutionResult(null, result.ErrorMessage, 0, false);
        }

        return new QueryExecutionResult("No results returned.\n", null, 0, true);
    }

    public async Task<string?> ValidateAsync(int dataSourceId, string sql, CancellationToken ct)
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
            return null;
        }

        return result.Errors is { Count: > 0 }
            ? string.Join("; ", result.Errors)
            : "Query validation failed.";
    }
}
