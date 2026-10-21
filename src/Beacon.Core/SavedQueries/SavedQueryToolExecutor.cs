using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.RowSecurity;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.SavedQueries;

/// <summary>The outcome of one saved-query tool run.</summary>
/// <param name="Rows">At most <see cref="MaxRows"/> rows, PII-masked as the project's MCP settings require.</param>
/// <param name="Truncated">True when the result had more than <see cref="MaxRows"/> rows.</param>
/// <param name="DataSourceId">The first step's data source, for the audit row.</param>
public sealed record SavedQueryToolExecution(
    bool Success,
    string? Error,
    IReadOnlyList<Dictionary<string, object?>> Rows,
    bool Truncated,
    int MaxRows,
    IReadOnlyList<string> TablesUsed,
    int? DataSourceId)
{
    public static SavedQueryToolExecution Failed(string error, IReadOnlyList<string> tablesUsed, int? dataSourceId) =>
        new(false, error, [], false, 0, tablesUsed, dataSourceId);
}

/// <summary>
/// Runs an approved saved-query version for an MCP caller through the read-only path (§1.5): every step's SQL — with
/// its arguments bound as database parameters (§1.10) — passes the shared <see cref="ISqlExecutionGate"/> (regex
/// guardrail → AST read-only → names in the source's own configured database → host allow-list policy for host-managed
/// sources → row limit) and executes through
/// <see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/>; a final query joins the step results in in-memory
/// SQLite behind the same gate. Unlike the UI's <c>QueryService</c> preview it never runs unbounded: the result is
/// capped at the project's MCP row limit, and an intermediate step that exceeds <see cref="IntermediateRowCap"/> fails
/// the call rather than feeding a silently incomplete join. Each statement (every step and the final query) runs within the
/// resolved MCP <c>StatementTimeoutSeconds</c>, and a step on a PostgreSQL source is refused before it runs when the
/// planner's estimated cost exceeds the resolved <c>MaxExplainCost</c> (<see cref="IQueryCostLimit"/>). Right before any SQL runs it re-checks the steps that run:
/// the caller may still run stored SQL in the project, and the project still links every data source they read;
/// otherwise the call fails without running anything.
/// </summary>
public interface ISavedQueryToolExecutor
{
    Task<SavedQueryToolExecution> ExecuteAsync(
        SavedQueryToolDefinition tool,
        int projectId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);
}

internal sealed class SavedQueryToolExecutor(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceProviderFactory providerFactory,
    ISqlExecutionGate gate,
    IConfiguredDatabaseResolver configuredDatabases,
    IQueryGuardrailService guardrailService,
    ISharedSourceSettings sharedSourceSettings,
    IProjectAccessEvaluator projectAccess,
    IRowSecurityResolver rowSecurityResolver,
    IQueryCostLimit costLimit,
    ILoggerFactory loggerFactory) : ISavedQueryToolExecutor
{
    /// <summary>The most rows one intermediate step of a multi-step query may return before the call fails.</summary>
    public const int IntermediateRowCap = 50_000;

    private const string SqliteDialect = "SQLite";

    private readonly ILogger _logger = loggerFactory.CreateLogger<SavedQueryToolExecutor>();

    public async Task<SavedQueryToolExecution> ExecuteAsync(
        SavedQueryToolDefinition tool,
        int projectId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        // The strictest guardrails of the live projects that link any of the steps' data sources apply.
        var settings = await sharedSourceSettings.GetEffectiveSettingsAsync(projectId, tool.DataSourceIds.ToList(), cancellationToken);
        var maxRows = Math.Max(1, settings.MaxRowLimit);
        var firstDataSourceId = tool.Steps.Count > 0 ? tool.Steps[0].DataSourceId : (int?)null;
        var tablesUsed = new List<string>();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var refusal = await RefusalAsync(context, tool, projectId, cancellationToken);
        if (refusal != null)
        {
            return SavedQueryToolExecution.Failed(refusal, tablesUsed, firstDataSourceId);
        }

        var dataSourceIds = tool.DataSourceIds.ToList();
        var dataSources = await context.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        // Only the final result is capped at the MCP row limit; with a final query the steps feed the join and are
        // bounded by the intermediate cap instead.
        var hasFinalQuery = tool.FinalQuery != null;
        var stepResults = new List<(int StepOrder, DataSource DataSource, List<Dictionary<string, object?>> Rows)>();
        var customPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;
        // The steps' PII result columns (the SQL's PII names and host-masked columns, and with detection on their own
        // names and their SQL's aliasing): the final query reads them back by name, and its result columns filled from
        // them are masked (lineage).
        var joinPiiColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in tool.Steps)
        {
            var dataSource = dataSources
                .Where(x => x.Id == step.DataSourceId)
                .FirstOrDefault();

            if (dataSource == null)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder}: its data source is no longer available.", tablesUsed, firstDataSourceId);
            }

            if (dataSource.DataSourceType != DataSourceType.Database || !dataSource.DatabaseEngineType.HasValue)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder}: saved-query tools run SQL against database data sources only.", tablesUsed, firstDataSourceId);
            }

            var (boundSql, parameters) = SavedQueryParameterBinder.Bind(step.SqlValue, step.Parameters, arguments);
            var isResultStep = !hasFinalQuery && step == tool.Steps[^1];
            var stepCap = isResultStep ? maxRows : IntermediateRowCap;
            var rowSecurity = await rowSecurityResolver.ResolveAsync(dataSource.Id, cancellationToken);

            var report = gate.Evaluate(SqlGateRequest.FromSettings(boundSql, DataSourceSqlDialect.Of(dataSource), settings, rowSecurity) with
            {
                EnforceReadOnly = true,
                MaxRows = stepCap + 1,
                HostManagedKey = dataSource.HostManagedKey,
                ConfiguredDatabase = configuredDatabases.Resolve(dataSource)
            });
            tablesUsed.AddRange(report.TablesUsed);

            if (report.Blocked)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} failed validation: {report.BlockReason ?? "the SQL was rejected."}", tablesUsed, firstDataSourceId);
            }

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(settings.StatementTimeoutSeconds));

            // MaxExplainCost (PostgreSQL only): the planner's estimate for the statement as it runs, its parameters bound,
            // within the same timeout. A failed estimate fails the step as a failing statement would.
            var cost = await costLimit.CheckAsync(dataSource, report.FinalSql, parameters, settings.MaxExplainCost, timeoutCts.Token);
            if (cost.Refusal != null)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder}: {cost.Refusal}", tablesUsed, firstDataSourceId);
            }

            if (cost.EstimateError != null)
            {
                var estimateError = DatabaseErrorRedactor.ForCaller(
                    cost.EstimateError,
                    report.FinalSql,
                    settings.EnablePiiDetection && dataSource.HostManagedKey == null);

                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} failed: {estimateError}", tablesUsed, firstDataSourceId);
            }

            // §1.5 backstop — read-only execution path (a READ ONLY transaction on PostgreSQL and MySQL; parser gates
            // above for the other engines, see IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement).
            var result = await provider.ExecuteReadOnlyQueryAsync(dataSource, report.FinalSql, parameters, timeoutCts.Token);
            if (!result.Success)
            {
                // The database's error can quote row values: redacted while PII detection is on (a host data source's error is
                // already Beacon's generic text).
                var error = DatabaseErrorRedactor.ForCaller(
                    result.ErrorMessage ?? "unknown error",
                    report.FinalSql,
                    settings.EnablePiiDetection && dataSource.HostManagedKey == null);

                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} failed: {error}", tablesUsed, firstDataSourceId);
            }

            var rows = result.Rows ?? [];
            if (!isResultStep && rows.Count > IntermediateRowCap)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} returned more than {IntermediateRowCap} rows; the joined result would be incomplete.", tablesUsed, firstDataSourceId);
            }

            var dialect = DataSourceSqlDialect.Of(dataSource);
            if (hasFinalQuery)
            {
                // Rows that feed the final query stay as read inside the in-memory join, which they never leave
                // (host-masked by the provider): masked, two distinct keys could mask alike (two e-mail addresses, an
                // address_id) and join as equal. The final query's rows are masked by their lineage instead.
                joinPiiColumns.UnionWith(PiiRowMasker.ResolvePiiColumns(
                    guardrailService,
                    rows.SelectMany(x => x.Keys),
                    report.PiiColumns,
                    settings.EnablePiiDetection,
                    customPatterns,
                    report.FinalSql,
                    dialect));
            }
            else
            {
                // Mask PII before rows leave the step (§1.6/§1.11). report.PiiColumns carries the SQL-text matches and the
                // host-masked columns (even with detection off); the result's columns are also masked by their own names
                // and aliases.
                rows = PiiRowMasker.Mask(
                    guardrailService,
                    rows,
                    report.PiiColumns,
                    isResultStep && settings.EnablePiiDetection,
                    customPatterns,
                    report.FinalSql,
                    dialect);
            }

            stepResults.Add((step.StepOrder, dataSource, rows));
        }

        if (!hasFinalQuery)
        {
            return Complete(stepResults[^1].Rows, maxRows, tablesUsed, firstDataSourceId);
        }

        return await ExecuteFinalQueryAsync(tool, stepResults, joinPiiColumns, settings, maxRows, tablesUsed, firstDataSourceId, cancellationToken);
    }

    private async Task<SavedQueryToolExecution> ExecuteFinalQueryAsync(
        SavedQueryToolDefinition tool,
        List<(int StepOrder, DataSource DataSource, List<Dictionary<string, object?>> Rows)> stepResults,
        IReadOnlySet<string> joinPiiColumns,
        Models.McpSettingsData settings,
        int maxRows,
        List<string> tablesUsed,
        int? firstDataSourceId,
        CancellationToken cancellationToken)
    {
        using var memDb = new InMemoryDatabaseManager(loggerFactory.CreateLogger<InMemoryDatabaseManager>());
        foreach (var (stepOrder, dataSource, rows) in stepResults)
        {
            await memDb.CreateTableFromResults(
                $"result{stepOrder}",
                rows
                    .Select(x => (IDictionary<string, object?>)x)
                    .ToList(),
                new ProjectInfo
                {
                    Name = dataSource.Name,
                    DatabaseEngine = dataSource.DatabaseEngineType!.Value.ToString(),
                    DatabaseEngineType = dataSource.DatabaseEngineType.Value
                });
        }

        var translated = memDb.TranslateFinalQuery(tool.FinalQuery!);
        var report = gate.Evaluate(SqlGateRequest.ForInMemoryJoin(translated, settings) with
        {
            EnforceReadOnly = true,
            MaxRows = maxRows + 1
        });

        if (report.Blocked)
        {
            return SavedQueryToolExecution.Failed($"The final query failed validation: {report.BlockReason ?? "the SQL was rejected."}", tablesUsed, firstDataSourceId);
        }

        var (results, _, timedOut) = await memDb.ExecuteQueryAsync(report.FinalSql, settings.StatementTimeoutSeconds, maxRows + 1, cancellationToken);
        if (timedOut)
        {
            return SavedQueryToolExecution.Failed("The final query timed out.", tablesUsed, firstDataSourceId);
        }

        // The final rows are masked by the lineage of the steps' PII columns — every result column the final query fills
        // from one, by its name, an alias or an expression over it, and every result column where its select list cannot
        // be traced — whatever the detection setting, and with detection on also by the final SQL's text matches,
        // PII-named result columns and its aliases (§1.6/§1.11).
        var joinedRows = results
            .Select(x => new Dictionary<string, object?>(x))
            .ToList();
        var lineage = PiiRowMasker.ResolveLineage(joinedRows.SelectMany(x => x.Keys), joinPiiColumns, report.FinalSql, SqliteDialect);
        var finalRows = PiiRowMasker.Mask(
            guardrailService,
            joinedRows,
            report.PiiColumns.Concat(lineage),
            settings.EnablePiiDetection,
            settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null,
            report.FinalSql,
            SqliteDialect);

        return Complete(finalRows, maxRows, tablesUsed, firstDataSourceId);
    }

    // The tool was listed for the caller earlier in the call; what runs is decided now, on the steps that run: the caller
    // may still run stored SQL in the project, and the project still links every (live) data source those steps read.
    private async Task<string?> RefusalAsync(BeaconContext context, SavedQueryToolDefinition tool, int projectId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var sources = tool.Steps
            .Select(x => x.DataSourceId)
            .Distinct()
            .ToList();
        var holdsEverySource = false;
        if (scope.Allows(projectId, ProjectAction.Run) && sources.Count > 0)
        {
            var linked = await SavedQueryToolSource.BuildMembershipQuery(context, [projectId], sources)
                .Select(x => x.DataSourceId)
                .ToListAsync(cancellationToken);
            holdsEverySource = sources.All(linked.Contains);
        }

        if (holdsEverySource)
        {
            return null;
        }

        _logger.LogWarning("Saved-query tool for query {QueryId} refused in project {ProjectId}: no longer runnable there for the caller", tool.QueryId, projectId);

        return $"not available in project {projectId} for your credentials.";
    }

    private SavedQueryToolExecution Complete(List<Dictionary<string, object?>> rows, int maxRows, List<string> tablesUsed, int? firstDataSourceId)
    {
        var truncated = rows.Count > maxRows;
        var capped = truncated
            ? rows
                .Take(maxRows)
                .ToList()
            : rows;

        _logger.LogInformation("Saved-query tool returned {RowCount} rows (truncated: {Truncated})", capped.Count, truncated);

        return new SavedQueryToolExecution(true, null, capped, truncated, maxRows, tablesUsed.Distinct().ToList(), firstDataSourceId);
    }
}
