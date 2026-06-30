using System.ComponentModel;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectQueryTool(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    SqlReadOnlyAstValidator readOnlyAstValidator,
    IMcpSettingsProvider settingsProvider,
    IProjectContext projectContext,
    McpProjectContextManager sessionManager,
    McpAuditService auditService,
    McpSignalService signalService)
{
    [McpServerTool(Name = "query")]
    [Description("Execute a query against a specific data source within the project. For databases: pass SQL. For API sources: pass a JSON query definition.")]
    public async Task<CallToolResult> ExecuteAsync(
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

        var signal = new McpSignalBuilder()
            .SetTool("query")
            .SetQuestion(sql ?? api_query ?? "")
            .SetUserId(projectContext.UserId);

        var resolveError = ToolHelper.ResolveProjectId(projectContext, sessionManager, project_id, out var projectId);
        if (resolveError != null)
            return await FailAsync(signal, sw, null, null, sql ?? api_query, resolveError, cancellationToken);

        signal.SetProjectId(projectId);

        // Resolve data source by name or ID
        if (datasource_id == null && string.IsNullOrEmpty(datasource_name))
            return await FailAsync(signal, sw, projectId, null, sql ?? api_query, "Provide either datasource_name or datasource_id.", cancellationToken);

        if (datasource_id == null && !string.IsNullOrEmpty(datasource_name))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, datasource_name, cancellationToken);
            if (nameError != null)
                return await FailAsync(signal, sw, projectId, null, sql ?? api_query, nameError, cancellationToken);
            datasource_id = resolvedId;
        }

        signal.SetDataSourceId(datasource_id);

        var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, datasource_id!.Value, cancellationToken);
        if (projectError != null)
            return await FailAsync(signal, sw, projectId, datasource_id, sql ?? api_query, projectError, cancellationToken);

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
                    return await FailAsync(signal, sw, projectId, datasource_id, sql ?? api_query,
                        "Missing required parameter: api_query (JSON query definition for API data sources)", cancellationToken);
            }
            else
            {
                if (string.IsNullOrEmpty(sql))
                    return await FailAsync(signal, sw, projectId, datasource_id, sql ?? api_query,
                        "Missing required parameter: sql", cancellationToken);

                signal.SetGeneratedSql(sql, SqlParsingHelper.ExtractTableNamesFromSql(sql));

                var validation = guardrailService.ValidateQuery(sql, new QueryGuardrailOptions
                {
                    ReadOnly = settings.EnforceReadOnly,
                    DetectPii = settings.EnablePiiDetection,
                    CustomPiiPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null
                });
                if (!validation.IsValid)
                {
                    sw.Stop();
                    signal.SetExecutionFailed(validation.Error ?? "Query validation failed");
                    signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
                    await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                        sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, validation.Error, cancellationToken);
                    await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
                    return ToolHelper.Error($"Query validation failed: {validation.Error}");
                }

                // AST-based read-only defense-in-depth on top of the regex guardrail (§1.5)
                if (settings.EnforceReadOnly)
                {
                    var astError = readOnlyAstValidator.Validate(sql, dataSource.DatabaseEngineType?.ToString());
                    if (astError != null)
                    {
                        sw.Stop();
                        signal.SetExecutionFailed(astError);
                        signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
                        await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                            sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, astError, cancellationToken);
                        await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
                        return ToolHelper.Error($"Query validation failed: {astError}");
                    }
                }

                queryText = guardrailService.ApplyRowLimit(sql, maxRows, dataSource.DatabaseEngineType?.ToString());
            }

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));
            var result = await provider.ExecuteQueryAsync(dataSource, queryText, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
            {
                sw.Stop();
                signal.SetExecutionFailed(result.ErrorMessage ?? "Unknown error");
                signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
                await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                    sql ?? api_query, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, result.ErrorMessage, cancellationToken);
                await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
                return ToolHelper.Error($"Query execution failed: {result.ErrorMessage}");
            }

            var text = $"# Query Results\n\n**Data Source:** {dataSource.Name}\n**Rows:** {result.Rows?.Count ?? 0}\n\n";

            if (result.Rows != null && result.Rows.Count > 0)
                text += ToolHelper.FormatResultsAsMarkdown(result.Rows, maxRows);

            sw.Stop();
            signal.SetResult(result.Rows?.Count, (int)sw.ElapsedMilliseconds, true);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                sql ?? api_query, datasource_id, projectId, (int)sw.ElapsedMilliseconds, result.Rows?.Count, null, cancellationToken);
            await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
            return ToolHelper.Success(text);
        }
        catch (Exception ex)
        {
            sw.Stop();
            signal.SetExecutionFailed(ex.Message);
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                sql ?? api_query, datasource_id, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            await signalService.RecordSignalAsync(signal.Build(), CancellationToken.None);
            return ToolHelper.Error($"Query execution failed: {ex.Message}");
        }
    }

    // §1.7 / §9.5 — audit + signal must be recorded on every outcome, including the early-exit
    // failures that occur before the main execution path (project/data-source resolution, access
    // denied, missing input). Mirrors ProjectAskTool.FailAsync.
    private async Task<CallToolResult> FailAsync(
        McpSignalBuilder signal,
        Stopwatch sw,
        int? projectId,
        int? dataSourceId,
        string? queryText,
        string error,
        CancellationToken cancellationToken)
    {
        sw.Stop();
        signal.SetExecutionFailed(error);
        signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
        await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
            queryText, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, error, cancellationToken);
        await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
        return ToolHelper.Error(error);
    }
}
