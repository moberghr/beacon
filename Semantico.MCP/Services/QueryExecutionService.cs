using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;
using Semantico.MCP.Tools;

namespace Semantico.MCP.Services;

internal sealed class QueryExecutionService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService) : IQueryExecutionService
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
            var text = $"### Results ({result.Rows.Count} rows)\n\n";
            text += ToolHelper.FormatResultsAsMarkdown(result.Rows);

            return new QueryExecutionResult(text, null, result.Rows.Count, true);
        }

        if (!result.Success)
        {
            return new QueryExecutionResult(null, result.ErrorMessage, 0, false);
        }

        return new QueryExecutionResult("No results returned.\n", null, 0, true);
    }
}
