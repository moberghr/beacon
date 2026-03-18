using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;
using Semantico.MCP.Protocol;

namespace Semantico.MCP.Tools;

internal sealed class ProjectQueryTool(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider) : IMcpTool
{
    public string Name => "query";
    public string Description => "Execute a query against a specific data source within the project. For databases: pass SQL. For API sources: pass a JSON query definition.";
    public object InputSchema => ToolHelper.SchemaObject(
        new Dictionary<string, object>
        {
            ["datasource_name"] = ToolHelper.StringProp("Name of the data source to query (preferred)"),
            ["datasource_id"] = ToolHelper.IntProp("ID of the data source to query (alternative to name)"),
            ["sql"] = ToolHelper.StringProp("The SQL query to execute (SELECT only) — for database data sources"),
            ["api_query"] = ToolHelper.StringProp("JSON query definition for API data sources: { method, path, parameters, resultMapping }"),
            ["max_rows"] = ToolHelper.IntProp("Maximum rows to return (default: 100, max: 1000)"),
            ["project_id"] = ToolHelper.IntProp("Optional. Specify project if your API key has access to multiple projects.")
        });

    public async Task<McpToolResult> ExecuteAsync(JsonElement? arguments, McpClientSession session, CancellationToken ct)
    {
        var requestedProjectId = ToolHelper.GetInt(arguments, "project_id");
        var resolveError = ToolHelper.ResolveProjectId(session, requestedProjectId, out var projectId);
        if (resolveError != null) return resolveError;

        // Resolve data source by name or ID
        var dsName = ToolHelper.GetString(arguments, "datasource_name");
        var dsId = ToolHelper.GetInt(arguments, "datasource_id");

        if (dsId == null && string.IsNullOrEmpty(dsName))
            return ToolHelper.ErrorResult("Provide either datasource_name or datasource_id.");

        if (dsId == null && !string.IsNullOrEmpty(dsName))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, dsName, ct);
            if (nameError != null) return nameError;
            dsId = resolvedId;
        }

        // Validate the data source belongs to this project
        var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, dsId!.Value, ct);
        if (projectError != null) return projectError;

        var settings = await settingsProvider.GetSettingsAsync(ct);
        var maxRows = Math.Min(ToolHelper.GetInt(arguments, "max_rows") ?? 100, settings.MaxRowLimit);

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dsId.Value, ct)
                ?? throw new InvalidOperationException($"Data source {dsId} not found");

            var isApi = dataSource.DataSourceType == DataSourceType.Api;
            string queryText;

            if (isApi)
            {
                var apiQuery = ToolHelper.GetString(arguments, "api_query");
                var sql = ToolHelper.GetString(arguments, "sql");
                queryText = apiQuery ?? sql ?? "";
                if (string.IsNullOrEmpty(queryText))
                    return ToolHelper.ErrorResult("Missing required parameter: api_query (JSON query definition for API data sources)");
            }
            else
            {
                var sql = ToolHelper.GetString(arguments, "sql");
                if (string.IsNullOrEmpty(sql))
                    return ToolHelper.ErrorResult("Missing required parameter: sql");

                var validation = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                {
                    ReadOnly = settings.EnforceReadOnly,
                    DetectPii = settings.EnablePiiDetection,
                    CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                });
                if (!validation.IsValid)
                    return ToolHelper.ErrorResult($"Query validation failed: {validation.Error}");

                var engine = dataSource.DatabaseEngineType?.ToString();
                queryText = guardrailService.ApplyRowLimit(sql, maxRows, engine);
            }

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await provider.ExecuteQueryAsync(dataSource, queryText, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
                return ToolHelper.ErrorResult($"Query execution failed: {result.ErrorMessage}");

            var text = $"# Query Results\n\n**Data Source:** {dataSource.Name}\n**Rows:** {result.Rows?.Count ?? 0}\n\n";

            if (result.Rows != null && result.Rows.Count > 0)
                text += ToolHelper.FormatResultsAsMarkdown(result.Rows, maxRows);

            return ToolHelper.TextResult(text);
        }
        catch (Exception ex)
        {
            return ToolHelper.ErrorResult($"Error executing query: {ex.Message}");
        }
    }
}
