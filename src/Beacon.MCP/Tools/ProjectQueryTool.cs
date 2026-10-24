using System.ComponentModel;
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
using Beacon.Core.RowSecurity;
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
    IRowSecurityResolver rowSecurityResolver,
    IKnowledgeGraphService knowledgeGraph,
    ISharedSourceSettings sharedSourceSettings,
    IProjectContext projectContext,
    McpToolCallRecorder recorder,
    McpQueryConcurrencyLimiter concurrencyLimiter,
    IQueryCostLimit costLimit,
    ILogger<ProjectQueryTool> logger)
{
    /// <summary>
    /// What `query` answers for an ordinary data source with no extracted metadata: without a catalog the schema check
    /// cannot run, so the statement is refused instead of reaching the source unchecked.
    /// </summary>
    public const string NoMetadataMessage =
        "This data source has no extracted metadata (it was never scanned, metadata loading is off, or the scan found no tables); query it through MCP once it has metadata.";

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
        // Written by McpAuditCallToolFilter after the tool returns or throws (§1.7); the signal too, on every outcome (§9.5).
        recorder.Start("query", projectContext.UserId, sql ?? api_query);

        var signal = new McpSignalBuilder()
            .SetTool("query")
            .SetQuestion(sql ?? api_query ?? "")
            .SetUserId(projectContext.UserId);
        recorder.WithSignal(signal, sql ?? api_query);

        var resolveError = ToolHelper.ResolveProjectId(projectContext, "query", project_id, out var projectId);
        if (resolveError != null)
            return Fail(null, null, resolveError);

        signal.SetProjectId(projectId);
        recorder.WithProject(projectId);

        // Resolve data source by name or ID
        if (datasource_id == null && string.IsNullOrEmpty(datasource_name))
            return Fail(projectId, null, "Provide either datasource_name or datasource_id.");

        if (datasource_id == null && !string.IsNullOrEmpty(datasource_name))
        {
            var (resolvedId, nameError) = await ToolHelper.ResolveDataSourceByNameAsync(contextFactory, projectId, datasource_name, cancellationToken);
            if (nameError != null)
                return Fail(projectId, null, nameError);
            datasource_id = resolvedId;
        }

        signal.SetDataSourceId(datasource_id);
        recorder.WithDataSource(datasource_id);

        var projectError = await ToolHelper.ValidateDataSourceInProjectAsync(contextFactory, projectId, datasource_id!.Value, cancellationToken);
        if (projectError != null)
            return Fail(projectId, datasource_id, projectError);

        // The strictest guardrails of the live projects that link the data source apply, whichever project asks.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectId, [datasource_id.Value], cancellationToken);
        // The cap is at least one row: a max_rows of zero or below never removes it.
        var maxRows = Math.Clamp(max_rows ?? 100, 1, Math.Max(1, settings.MaxRowLimit));

        // MaxConcurrentQueriesPerKey: refused at once, never queued, and audited like every other refusal (§1.7/§9.5).
        using var lease = concurrencyLimiter.TryAcquire(McpQueryConcurrencyLimiter.CallerKeyOf(projectContext), settings.MaxConcurrentQueriesPerKey);
        if (lease == null)
        {
            return Fail(projectId, datasource_id, McpQueryConcurrencyLimiter.RefusalMessage);
        }

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
                return Fail(projectId, datasource_id, "Missing required parameter: api_query (JSON query definition for API data sources)");
        }
        else
        {
            if (string.IsNullOrEmpty(sql))
                return Fail(projectId, datasource_id, "Missing required parameter: sql");

            // One shared gate (§1.5): regex guardrail → AST read-only → schema catalog (blocking here — a
            // hallucinated column is refused before it reaches the warehouse) → row limit. Tables come
            // from the AST walk (aliases resolved, CTEs excluded), not from a regex over the text.
            var catalog = await knowledgeGraph.GetSchemaCatalogAsync(datasource_id.Value, projectId, cancellationToken);
            var rowSecurity = await rowSecurityResolver.ResolveAsync(datasource_id.Value, cancellationToken);
            report = gate.Evaluate(SqlGateRequest.FromSettings(sql, DataSourceSqlDialect.Of(dataSource), settings, rowSecurity) with
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
                recorder.Fail(blockReason, report.TablesUsed.ToList());
                return ToolHelper.Error($"Query validation failed: {blockReason}");
            }

            // `query` blocks on the catalog, so a source with none (never scanned) is refused, not run unchecked. A host
            // data source keeps its own allow-list policy; `ask`, cross-source and `dry_run` stay advisory.
            if (report.Verdicts.Schema.Code == SqlGateCodes.EmptyCatalog && dataSource.HostManagedKey == null)
            {
                return Fail(projectId, datasource_id, NoMetadataMessage, report.TablesUsed.ToList());
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
            return Fail(projectId, datasource_id, cost.Refusal, report?.TablesUsed.ToList());
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
            recorder.Fail(
                callerError,
                report?.TablesUsed.ToList(),
                DatabaseErrorRedactor.ForCaller(rawError, queryText, redact: true, logger));
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

        recorder.Succeed(result.Rows?.Count, report?.TablesUsed.ToList());
        return ToolHelper.Success(text, structured);
    }

    // §1.7 / §9.5 — audit + signal must be recorded on every outcome, including the early-exit
    // failures that occur before the main execution path (project/data-source resolution, access
    // denied, missing input). The recorder writes the error on the signal too. Mirrors ProjectAskTool.Fail.
    private CallToolResult Fail(int? projectId, int? dataSourceId, string error, IReadOnlyList<string>? tables = null)
    {
        recorder
            .WithProject(projectId)
            .WithDataSource(dataSourceId)
            .Fail(error, tables);

        return ToolHelper.Error(error);
    }
}
