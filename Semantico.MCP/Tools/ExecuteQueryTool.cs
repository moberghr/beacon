using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Services;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class ExecuteQueryTool(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider) : IMcpTool
{
    public string Name => "query";
    public string Description => "Execute a read-only SQL query against a data source. Queries are validated for safety and limited in row count.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["datasource_id"] = ToolHelper.IntProp("The data source ID to query"),
            ["sql"] = ToolHelper.StringProp("The SQL query to execute (SELECT only)"),
            ["max_rows"] = ToolHelper.IntProp("Maximum rows to return (default: 100, max: 1000)")
        },
        ["datasource_id", "sql"]);

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var dsId = ToolHelper.GetInt(arguments, "datasource_id");
        var sql = ToolHelper.GetString(arguments, "sql");

        if (dsId == null || string.IsNullOrEmpty(sql))
            return ToolHelper.ErrorResult("Missing required parameters: datasource_id, sql");

        var scopeError = ToolHelper.ValidateDataSourceAccess(session, dsId.Value);
        if (scopeError != null) return scopeError;

        var settings = await settingsProvider.GetSettingsAsync(ct);
        var maxRows = Math.Min(ToolHelper.GetInt(arguments, "max_rows") ?? 100, settings.MaxRowLimit);

        // Validate query
        var validation = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
        {
            ReadOnly = settings.EnforceReadOnly,
            DetectPii = settings.EnablePiiDetection,
            CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
        });
        if (!validation.IsValid)
            return ToolHelper.ErrorResult($"Query validation failed: {validation.Error}");

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dsId.Value, ct)
                ?? throw new InvalidOperationException($"Data source {dsId} not found");

            // Apply row limit
            var engine = dataSource.DatabaseEngineType?.ToString();
            var limitedSql = guardrailService.ApplyRowLimit(sql, maxRows, engine);

            // Execute with timeout
            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await provider.ExecuteQueryAsync(dataSource, limitedSql, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
                return ToolHelper.ErrorResult($"Query execution failed: {result.ErrorMessage}");

            // Format results as markdown table
            var text = $"# Query Results\n\n**Rows:** {result.Rows?.Count ?? 0}\n\n";

            if (result.Rows != null && result.Rows.Count > 0)
            {
                // Detect and mask PII
                var columns = result.Rows[0].Keys.ToList();
                var piiCols = validation.PiiColumns ?? new();

                // Header
                text += "| " + string.Join(" | ", columns) + " |\n";
                text += "| " + string.Join(" | ", columns.Select(_ => "---")) + " |\n";

                // Rows
                foreach (var row in result.Rows.Take(maxRows))
                {
                    var maskedRow = piiCols.Count > 0
                        ? guardrailService.MaskPiiValues(row, piiCols)
                        : row;
                    text += "| " + string.Join(" | ", columns.Select(c =>
                        maskedRow.TryGetValue(c, out var v) ? (v?.ToString() ?? "NULL") : "NULL")) + " |\n";
                }

                if (piiCols.Count > 0)
                    text += $"\n*Note: PII columns masked: {string.Join(", ", piiCols)}*\n";
            }

            return ToolHelper.TextResult(text);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error executing query: {ex.Message}");
        }
    }
}
