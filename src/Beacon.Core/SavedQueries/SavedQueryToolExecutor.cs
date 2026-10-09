using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
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
/// guardrail → AST read-only → host allow-list policy for host-managed sources → row limit) and executes through
/// <see cref="IDataSourceProvider.ExecuteReadOnlyQueryAsync"/>; a final query joins the step results in in-memory
/// SQLite behind the same gate. Unlike the UI's <c>QueryService</c> preview it never runs unbounded: the result is
/// capped at the project's MCP row limit, and an intermediate step that exceeds <see cref="IntermediateRowCap"/> fails
/// the call rather than feeding a silently incomplete join.
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
    IQueryGuardrailService guardrailService,
    IMcpSettingsProvider settingsProvider,
    ILoggerFactory loggerFactory) : ISavedQueryToolExecutor
{
    /// <summary>The most rows one intermediate step of a multi-step query may return before the call fails.</summary>
    public const int IntermediateRowCap = 50_000;

    private const int StepTimeoutSeconds = 30;
    private const string SqliteDialect = "SQLite";

    private readonly ILogger _logger = loggerFactory.CreateLogger<SavedQueryToolExecutor>();

    public async Task<SavedQueryToolExecution> ExecuteAsync(
        SavedQueryToolDefinition tool,
        int projectId,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId, cancellationToken);
        var maxRows = Math.Max(1, settings.MaxRowLimit);
        var firstDataSourceId = tool.Steps.Count > 0 ? tool.Steps[0].DataSourceId : (int?)null;
        var tablesUsed = new List<string>();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dataSourceIds = tool.DataSourceIds.ToList();
        var dataSources = await context.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .ToListAsync(cancellationToken);

        // Only the final result is capped at the MCP row limit; with a final query the steps feed the join and are
        // bounded by the intermediate cap instead.
        var hasFinalQuery = tool.FinalQuery != null;
        var stepResults = new List<(int StepOrder, DataSource DataSource, List<Dictionary<string, object?>> Rows)>();
        var customPatterns = settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;
        // The steps' PII result columns (by name or by their SQL's aliasing), masked on the final query's rows, which
        // read them back by name.
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

            var report = gate.Evaluate(SqlGateRequest.FromSettings(boundSql, DataSourceSqlDialect.Of(dataSource), settings) with
            {
                EnforceReadOnly = true,
                MaxRows = stepCap + 1,
                HostManagedKey = dataSource.HostManagedKey
            });
            tablesUsed.AddRange(report.TablesUsed);

            if (report.Blocked)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} failed validation: {report.BlockReason ?? "the SQL was rejected."}", tablesUsed, firstDataSourceId);
            }

            var provider = providerFactory.GetProvider(dataSource.DataSourceType);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(StepTimeoutSeconds));

            // §1.5 backstop — read-only execution path (a READ ONLY transaction on PostgreSQL and MySQL; parser gates
            // above for the other engines, see IDataSourceProvider.SupportsDatabaseReadOnlyEnforcement).
            var result = await provider.ExecuteReadOnlyQueryAsync(dataSource, report.FinalSql, parameters, timeoutCts.Token);
            if (!result.Success)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} failed: {result.ErrorMessage ?? "unknown error"}", tablesUsed, firstDataSourceId);
            }

            var rows = result.Rows ?? [];
            if (!isResultStep && rows.Count > IntermediateRowCap)
            {
                return SavedQueryToolExecution.Failed($"Step {step.StepOrder} returned more than {IntermediateRowCap} rows; the joined result would be incomplete.", tablesUsed, firstDataSourceId);
            }

            var dialect = DataSourceSqlDialect.Of(dataSource);
            if (hasFinalQuery && settings.EnablePiiDetection)
            {
                joinPiiColumns.UnionWith(PiiRowMasker.ResolvePiiColumns(
                    guardrailService,
                    rows.SelectMany(x => x.Keys),
                    report.PiiColumns,
                    detectByColumnName: true,
                    customPatterns,
                    report.FinalSql,
                    dialect));
            }

            // Mask PII before rows leave the step (§1.6/§1.11). report.PiiColumns carries the SQL-text matches and the
            // host-masked columns (even with detection off). Result-column + alias detection runs only on the rows
            // returned as the result: on rows that feed the in-memory join it would also hit join keys the unanchored
            // PII pattern matches (address_id, zip_code_id) and make distinct keys collide, so the final query's rows
            // get it instead.
            rows = PiiRowMasker.Mask(
                guardrailService,
                rows,
                report.PiiColumns,
                isResultStep && settings.EnablePiiDetection,
                customPatterns,
                report.FinalSql,
                dialect);

            stepResults.Add((step.StepOrder, dataSource, rows));
        }

        if (!hasFinalQuery)
        {
            return Complete(stepResults[^1].Rows, maxRows, tablesUsed, firstDataSourceId);
        }

        return await ExecuteFinalQueryAsync(tool, stepResults, joinPiiColumns, settings, maxRows, tablesUsed, firstDataSourceId);
    }

    private async Task<SavedQueryToolExecution> ExecuteFinalQueryAsync(
        SavedQueryToolDefinition tool,
        List<(int StepOrder, DataSource DataSource, List<Dictionary<string, object?>> Rows)> stepResults,
        IReadOnlySet<string> joinPiiColumns,
        Models.McpSettingsData settings,
        int maxRows,
        List<string> tablesUsed,
        int? firstDataSourceId)
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
        var report = gate.Evaluate(SqlGateRequest.FromSettings(translated, SqliteDialect, settings) with
        {
            EnforceReadOnly = true,
            MaxRows = maxRows + 1
        });

        if (report.Blocked)
        {
            return SavedQueryToolExecution.Failed($"The final query failed validation: {report.BlockReason ?? "the SQL was rejected."}", tablesUsed, firstDataSourceId);
        }

        var (results, _, timedOut) = await memDb.ExecuteQueryAsync(report.FinalSql, StepTimeoutSeconds);
        if (timedOut)
        {
            return SavedQueryToolExecution.Failed("The final query timed out.", tablesUsed, firstDataSourceId);
        }

        // The final rows get the full masking: the steps' PII columns read back by name, the final SQL's text matches,
        // PII-named result columns and the final SQL's aliases (§1.6/§1.11).
        var finalRows = PiiRowMasker.Mask(
            guardrailService,
            results
                .Select(x => new Dictionary<string, object?>(x))
                .ToList(),
            report.PiiColumns.Concat(joinPiiColumns),
            settings.EnablePiiDetection,
            settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null,
            report.FinalSql,
            SqliteDialect);

        return Complete(finalRows, maxRows, tablesUsed, firstDataSourceId);
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
