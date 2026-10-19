using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Services;
using Beacon.Core.Models.Providers;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class ProjectQueryTool(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    IQueryGuardrailService guardrailService,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IKnowledgeGraphService knowledgeGraph,
    ISharedSourceSettings sharedSourceSettings,
    IProjectContext projectContext,
    McpAuditService auditService,
    McpSignalService signalService,
    McpQueryConcurrencyLimiter concurrencyLimiter,
    IQueryCostLimit costLimit,
    ILogger<ProjectQueryTool> logger)
{
    [McpServerTool(Name = "query", Title = "Run Read-Only Query", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
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

        var resolveError = ToolHelper.ResolveProjectId(projectContext, "query", project_id, out var projectId);
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

        // The strictest guardrails of the live projects that link the data source apply, whichever project asks.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectId, [datasource_id.Value], cancellationToken);
        // The cap is at least one row: a max_rows of zero or below never removes it.
        var maxRows = Math.Clamp(max_rows ?? 100, 1, Math.Max(1, settings.MaxRowLimit));

        // MaxConcurrentQueriesPerKey: refused at once, never queued, and audited like every other refusal (§1.7/§9.5).
        using var lease = concurrencyLimiter.TryAcquire(McpQueryConcurrencyLimiter.CallerKeyOf(projectContext), settings.MaxConcurrentQueriesPerKey);
        if (lease == null)
        {
            return await FailAsync(signal, sw, projectId, datasource_id, sql ?? api_query, McpQueryConcurrencyLimiter.RefusalMessage, cancellationToken);
        }

        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == datasource_id.Value, cancellationToken)
                ?? throw new InvalidOperationException($"Data source {datasource_id} not found");

            var isApi = dataSource.DataSourceType == DataSourceType.Api;
            string queryText;
            // PII columns detected on the SQL path (null for API sources); used to mask result values below.
            List<string>? piiColumns = null;
            // Hoisted so the audit calls after the if/else (provider failure, success) can also pass the
            // AST-resolved tables; stays null for API sources, which have no gate report.
            SqlGateReport? report = null;

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

                // One shared gate (§1.5): regex guardrail → AST read-only → schema catalog (blocking here — a
                // hallucinated column is refused before it reaches the warehouse) → row limit. Tables come
                // from the AST walk (aliases resolved, CTEs excluded), not from a regex over the text.
                var catalog = await knowledgeGraph.GetSchemaCatalogAsync(datasource_id.Value, projectId, cancellationToken);
                report = gate.Evaluate(SqlGateRequest.FromSettings(sql, DataSourceSqlDialect.Of(dataSource), settings) with
                {
                    Catalog = catalog,
                    BlockOnSchemaFailure = true,
                    MaxRows = maxRows,
                    HostManagedKey = dataSource.HostManagedKey,
                    ConfiguredDatabase = configuredDatabases.Resolve(dataSource)
                });
                signal.SetGeneratedSql(sql, report.TablesUsed.ToList());

                if (report.Blocked)
                {
                    var blockReason = report.BlockReason ?? "Query validation failed";
                    sw.Stop();
                    signal.SetExecutionFailed(blockReason);
                    signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
                    await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                        sql, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, blockReason,
                        tables: report.TablesUsed.ToList(), ct: cancellationToken);
                    await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
                    return ToolHelper.Error($"Query validation failed: {blockReason}");
                }

                piiColumns = report.PiiColumns.Count > 0 ? report.PiiColumns.ToList() : null;
                queryText = report.FinalSql;
            }

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.StatementTimeoutSeconds));

            // MaxExplainCost (PostgreSQL only): the gate's FinalSql is planned, never run, within the statement timeout. A
            // refusal is audited and signalled like every other refusal (§1.7/§9.5); an estimate that failed fails the call
            // below exactly as a failing statement does.
            var cost = isApi
                ? QueryCostCheck.Pass
                : await costLimit.CheckAsync(dataSource, queryText, new Dictionary<string, object?>(), settings.MaxExplainCost, timeoutCts.Token);
            if (cost.Refusal != null)
            {
                return await FailAsync(signal, sw, projectId, datasource_id, sql, cost.Refusal, cancellationToken, report?.TablesUsed.ToList());
            }

            // §1.5 backstop — SQL executes through the read-only path. The database-level guarantee
            // is PostgreSQL and MySQL today (IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement);
            // other engines forward to normal execution and rely on the parser gates above. API
            // sources have no SQL engine, so they stay on the normal execution path.
            var result = cost.EstimateError != null
                ? new ProviderQueryResult { Success = false, ErrorMessage = cost.EstimateError }
                : isApi
                    ? await provider.ExecuteQueryAsync(dataSource, queryText, new Dictionary<string, object?>(), timeoutCts.Token)
                    : await provider.ExecuteReadOnlyQueryAsync(dataSource, queryText, new Dictionary<string, object?>(), timeoutCts.Token);

            if (!result.Success)
            {
                // The database's error can quote row values: the stored signal always gets it redacted, the caller and the
                // audit trail while PII detection is on (a host data source's error is already Beacon's generic text).
                var rawError = result.ErrorMessage ?? "Unknown error";
                var callerError = DatabaseErrorRedactor.ForCaller(rawError, queryText, settings.EnablePiiDetection && dataSource.HostManagedKey == null, logger);
                sw.Stop();
                signal.SetExecutionFailed(DatabaseErrorRedactor.ForCaller(rawError, queryText, redact: true, logger));
                signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
                await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                    sql ?? api_query, datasource_id, projectId, (int)sw.ElapsedMilliseconds, null, callerError,
                    tables: report?.TablesUsed.ToList(), ct: cancellationToken);
                await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
                return ToolHelper.Error($"Query execution failed: {callerError}");
            }

            var text = $"# Query Results\n\n**Data Source:** {dataSource.Name}\n**Rows:** {result.Rows?.Count ?? 0}\n\n";
            JsonNode? structured = null;

            if (result.Metadata.TryGetValue(HostResultMetadata.RenamedMaskedColumns, out var renamed) && renamed is int renamedCount and > 0)
            {
                text += $"*{renamedCount} column(s) of this host data source result were masked because the database named them differently than the host policy checked them.*\n\n";
            }

            if (result.Rows != null && result.Rows.Count > 0)
            {
                // Mask PII column values before returning to the MCP client (§1.6/§1.11): the SQL-text
                // matches, every result column whose name is PII, and the names the executed SQL aliases PII
                // columns to — so SELECT *, customer_email and email AS contact are covered. API sources have no
                // SQL to resolve aliases from. Host-masked columns are masked in full, as the provider masks them.
                var rows = PiiRowMasker.Mask(
                    guardrailService,
                    result.Rows,
                    piiColumns ?? [],
                    settings.EnablePiiDetection,
                    settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null,
                    isApi ? null : queryText,
                    DataSourceSqlDialect.Of(dataSource),
                    report?.HostMaskedColumns);

                // The text and the structured payload together stay within the resolved MaxResultBytes.
                (text, structured) = ToolHelper.FormatResultWithinBudget(text, rows, maxRows, settings.MaxResultBytes);
            }

            sw.Stop();
            signal.SetResult(result.Rows?.Count, (int)sw.ElapsedMilliseconds, true);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                sql ?? api_query, datasource_id, projectId, (int)sw.ElapsedMilliseconds, result.Rows?.Count, null,
                tables: report?.TablesUsed.ToList(), ct: cancellationToken);
            await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
            return ToolHelper.Success(text, structured);
        }
        catch (Exception ex)
        {
            sw.Stop();
            signal.SetExecutionFailed(DatabaseErrorRedactor.ForCaller(ex.Message, sql ?? api_query, redact: true, logger));
            signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
                sql ?? api_query, datasource_id, projectId == 0 ? null : projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, ct: CancellationToken.None);
            await signalService.RecordSignalAsync(signal.Build(), CancellationToken.None);
            // §1.11 — ex.Message can quote the user's SQL; type only here, full detail is in the audit log.
            logger.LogError("MCP tool {Tool} failed with {ExceptionType} (detail in MCP audit log)", "query", ex.GetType().Name);
            return ToolHelper.Error(ToolHelper.CallerSafeMessage(ex, "query"));
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
        CancellationToken cancellationToken,
        IReadOnlyList<string>? tables = null)
    {
        sw.Stop();
        signal.SetExecutionFailed(error);
        signal.SetResult(null, (int)sw.ElapsedMilliseconds, false);
        await auditService.LogToolCallAsync(null, projectContext.UserId, "query",
            queryText, dataSourceId, projectId, (int)sw.ElapsedMilliseconds, null, error, tables, cancellationToken);
        await signalService.RecordSignalAsync(signal.Build(), cancellationToken);
        return ToolHelper.Error(error);
    }
}
