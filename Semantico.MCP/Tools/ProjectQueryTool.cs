using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Server;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;
using Semantico.Core.Services.Providers;
using Semantico.Core.Services.Security;
using Semantico.MCP.Services;

namespace Semantico.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectQueryTool(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider,
    IProjectContext projectContext,
    McpProjectContextManager sessionManager,
    McpAuditService auditService)
{
    [McpServerTool(Name = "query")]
    [Description("Execute a query against a specific data source within the project. For databases: pass SQL. For API sources: pass a JSON query definition.")]
    public async Task<string> ExecuteAsync(
        [Description("Name of the data source to query (preferred)")]
        string? datasource_name = null,
        [Description("ID of the data source to query (alternative to name)")]
        int? datasource_id = null,
        [Description("The SQL query to execute (SELECT only) — for database data sources")]
        string? sql = null,
        [Description("JSON query definition for API data sources: { method, path, parameters, resultMapping }")]
        string? api_query = null,
        [Description("Maximum rows to return (default: 100, max: 1000)")]
        int? max_rows = null,
        [Description("Optional. Specify project if your API key has access to multiple projects.")]
        int? project_id = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var resolveError = ToolHelper.ResolveProjectId(projectContext, sessionManager, project_id, out var projectId);
        if (resolveError != null) return resolveError;

        // Resolve data source by name or ID
        if (datasource_id == null && string.IsNullOrEmpty(datasource_name))
            return "Provide either datasource_name or datasource_id.";

        if (datasource_id == null && !string.IsNullOrEmpty(datasource_name))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, datasource_name, cancellationToken);
            if (nameError != null) return nameError;
            datasource_id = resolvedId;
        }

        var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, datasource_id!.Value, cancellationToken);
        if (projectError != null) return projectError;

        var settings = await settingsProvider.GetSettingsAsync(cancellationToken);
        var maxRows = Math.Min(max_rows ?? 100, settings.MaxRowLimit);

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == datasource_id.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Data source {datasource_id} not found");

            var isApi = dataSource.DataSourceType == DataSourceType.Api;
            string queryText;

            if (isApi)
            {
                queryText = api_query ?? sql ?? "";
                if (string.IsNullOrEmpty(queryText))
                    return "Missing required parameter: api_query (JSON query definition for API data sources)";
            }
            else
            {
                if (string.IsNullOrEmpty(sql))
                    return "Missing required parameter: sql";

                var validation = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                {
                    ReadOnly = settings.EnforceReadOnly,
                    DetectPii = settings.EnablePiiDetection,
                    CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                });
                if (!validation.IsValid)
                    return $"Query validation failed: {validation.Error}";

                queryText = guardrailService.ApplyRowLimit(sql, maxRows, dataSource.DatabaseEngineType?.ToString());
            }

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await provider.ExecuteQueryAsync(dataSource, queryText, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
            {
                sw.Stop();
                _ = auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                    sql ?? api_query, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, result.ErrorMessage);
                return $"Query execution failed: {result.ErrorMessage}";
            }

            var text = $"# Query Results\n\n**Data Source:** {dataSource.Name}\n**Rows:** {result.Rows?.Count ?? 0}\n\n";

            if (result.Rows != null && result.Rows.Count > 0)
                text += ToolHelper.FormatResultsAsMarkdown(result.Rows, maxRows);

            sw.Stop();
            _ = auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                sql ?? api_query, datasource_id, projectId, (int)sw.ElapsedMilliseconds, result.Rows?.Count, null);
            return text;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _ = auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                sql ?? api_query, datasource_id, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message);
            return $"Error executing query: {ex.Message}";
        }
    }
}
