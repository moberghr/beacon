using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.File;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Validators;

namespace Beacon.Core.Services;

internal partial class QueryService
{
    /// <summary>Why a run whose final query ran out of time failed.</summary>
    internal const string FinalQueryTimedOutMessage = "The final query timed out.";

    public async Task<BaseResponse> AddQueryStep(int queryId, QueryStepData stepData, CancellationToken cancellationToken)
    {
        // The save check: the execution gate and, where metadata exists, the source's own tables and columns (§1.5).
        await EnsureStepsPassTheGateAsync([stepData], cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Stage parameters on the navigation collection so the step and its
        // parameters commit together in a single SaveChanges (§5.7).
        var queryStep = new QueryStep
        {
            QueryId = queryId,
            DataSourceId = stepData.DataSourceId,
            StepOrder = stepData.StepOrder,
            Name = stepData.Name,
            Description = stepData.Description,
            SqlValue = stepData.SqlValue,
            Parameters = ParameterEntityFactory.CreateQueryStepParameters(stepData.Parameters, 0)
        };

        context.QuerySteps.Add(queryStep);
        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Message = "Query step added successfully",
            Success = true
        };
    }

    public async Task<BaseResponse> UpdateQueryStep(int queryId, int stepOrder, QueryStepData stepData, CancellationToken cancellationToken)
    {
        // The save check: the execution gate and, where metadata exists, the source's own tables and columns (§1.5).
        await EnsureStepsPassTheGateAsync([stepData], cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var queryStep = await context.QuerySteps
            .Include(s => s.Parameters)
            .Where(s => s.QueryId == queryId && s.StepOrder == stepOrder)
            .SingleAsync(cancellationToken);

        queryStep.DataSourceId = stepData.DataSourceId;
        queryStep.Name = stepData.Name;
        queryStep.Description = stepData.Description;
        queryStep.SqlValue = stepData.SqlValue;

        // Archive existing parameters
        foreach (var parameter in queryStep.Parameters)
        {
            context.QueryStepParameters.Remove(parameter);
        }

        // Create new parameters
        var newParams = ParameterEntityFactory.CreateQueryStepParameters(stepData.Parameters, queryStep.Id);
        context.QueryStepParameters.AddRange(newParams);

        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Message = "Query step updated successfully",
            Success = true
        };
    }

    public async Task DeleteQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var queryStep = await context.QuerySteps
            .Where(s => s.QueryId == queryId && s.StepOrder == stepOrder)
            .SingleAsync(cancellationToken);

        // Cannot delete if it's the only step
        var stepCount = await context.QuerySteps.CountAsync(s => s.QueryId == queryId, cancellationToken);
        if (stepCount <= 1)
        {
            throw new BeaconException("Cannot delete the last remaining query step.");
        }

        context.QuerySteps.Remove(queryStep);
        await context.SaveChangesAsync(cancellationToken);
    }

    // ===== PRIVATE HELPER METHODS FOR CROSS-DATA-SOURCE EXECUTION =====

    private async Task<Query> GetQueryWithSteps(int queryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Queries
            .Include(q => q.Steps)
                .ThenInclude(s => s.DataSource)
            .Include(q => q.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(q => q.Id == queryId)
            .SingleAsync(cancellationToken);
    }

    /// <param name="pii">The settings to PII-mask by when the rows go on to recipients (a subscription run); null leaves
    /// rows as the provider returned them (host-masked).</param>
    /// <param name="statementTimeout">A shorter per-statement timeout than the runner's own (a subscription's), or null.</param>
    /// <param name="maxRows">The row ceiling of every statement in place of <see cref="ExecutionRowCeiling"/> (a
    /// migration's own budget), or null.</param>
    private async Task<QueryExecutionResult> ExecuteQuerySteps(
        Query query,
        string? finalQuery,
        List<ParameterValue>? parameters,
        McpSettingsData? pii,
        TimeSpan? statementTimeout,
        CancellationToken cancellationToken,
        int? maxRows = null)
    {
        var stepResults = new List<QueryStepResult>();
        using var virtualTableManager = new VirtualTableManager(loggerFactory.CreateLogger<VirtualTableManager>())
        {
            StatementTimeoutSeconds = FinalQueryTimeoutSeconds
        };
        var totalExecutionTime = 0.0;
        var dataSourceExecutionTimes = new Dictionary<string, double>();
        var dataSourceIdsRead = new List<int>();
        var hasFinalQuery = !string.IsNullOrEmpty(finalQuery);
        var steps = query.Steps
            .OrderBy(x => x.StepOrder)
            .ToList();
        // The steps' PII result columns (by name or by their SQL's aliasing), masked on the final query's rows, which read
        // them back by name.
        var joinPiiColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        logger.LogInformation("Executing query chain {QueryId}: {StepCount} steps across {DataSourceCount} data sources",
            query.Id, query.Steps.Count, query.DataSourceIds.Count);

        // Execute each step against its own database
        foreach (var step in steps)
        {
            logger.LogDebug("Executing step {StepOrder} against data source {DataSourceId} ({DatabaseEngine})",
                step.StepOrder, step.DataSourceId, step.DataSource.DatabaseEngineType);

            dataSourceIdsRead.Add(step.DataSourceId);

            // Only the rows delivered as the result are masked by their own column names and aliases: on rows that feed
            // the in-memory join that would also hit join keys the PII pattern matches (address_id), so the final rows
            // get it instead.
            var isResultStep = !hasFinalQuery && step == steps[^1];
            var masking = pii == null ? null : new StepMasking(pii, isResultStep);
            var (stepResult, run) = await ExecuteStep(step, parameters, masking, statementTimeout, cancellationToken, maxRows);
            stepResults.Add(stepResult);
            totalExecutionTime += stepResult.ExecutionTimeMs;

            // Track execution time by data source
            var dataSourceKey = $"{step.DataSource.Name} ({step.DataSource.DatabaseEngineType})";
            dataSourceExecutionTimes[dataSourceKey] = dataSourceExecutionTimes.GetValueOrDefault(dataSourceKey, 0) + stepResult.ExecutionTimeMs;

            if (stepResult.Success)
            {
                if (pii is { EnablePiiDetection: true } && hasFinalQuery)
                {
                    joinPiiColumns.UnionWith(PiiRowMasker.ResolvePiiColumns(
                        guardrail,
                        stepResult.AllResults.SelectMany(x => x.Keys),
                        run.Report.PiiColumns,
                        detectByColumnName: true,
                        CustomPatterns(pii),
                        run.Report.FinalSql,
                        run.Dialect));
                }

                // Add results to virtual table manager with data source context
                var dataSourceInfo = new ProjectInfo
                {
                    Name = step.DataSource.Name,
                    DatabaseEngine = step.DataSource.DatabaseEngineType?.ToString() ?? step.DataSource.DataSourceType.ToString(),
                    DatabaseEngineType = step.DataSource.DatabaseEngineType ?? DatabaseEngineType.PostgreSQL // Default fallback
                };

                virtualTableManager.AddVirtualTable($"@result{step.StepOrder}", stepResult.AllResults, dataSourceInfo);
            }
            else
            {
                logger.LogError("Step {StepOrder} of query {QueryId} failed", step.StepOrder, query.Id);
                break; // Stop execution on first failure
            }
        }

        QueryResult? finalResult = null;
        bool allStepsSucceeded = stepResults.All(s => s.Success);

        if (hasFinalQuery && allStepsSucceeded)
        {
            finalResult = await ExecuteFinalQuery(finalQuery!, virtualTableManager, pii, joinPiiColumns, cancellationToken, maxRows);
            totalExecutionTime += finalResult.ExecutionTimeMs;
        }
        else if (query.Steps.Count == 1 && string.IsNullOrEmpty(finalQuery) && allStepsSucceeded)
        {
            // Single-step query - convert step result to QueryResult
            finalResult = ConvertStepToQueryResult(stepResults[0], query);
        }

        var executionResult = new QueryExecutionResult
        {
            StepResults = stepResults,
            FinalResult = finalResult,
            Success = allStepsSucceeded,
            // Execution stops at the first failed step, so its message is the cause of the whole run.
            ErrorMessage = stepResults
                .Where(x => !x.Success)
                .Select(x => x.ErrorMessage)
                .FirstOrDefault(),
            TotalExecutionTimeMs = totalExecutionTime,
            IsMultiStep = query.IsMultiStep,
            IsCrossDataSource = query.IsCrossDataSource,
            IsCrossDatabase = query.IsCrossDatabase,
            DataSourcesInvolved = stepResults.Select(s => s.DataSourceName).Distinct().ToList(),
            DataSourceIds = dataSourceIdsRead
                .Distinct()
                .Order()
                .ToList(),
            DatabaseEnginesUsed = stepResults
                .Select(s => s.DatabaseEngineType)
                .Distinct()
                .ToList(),
            ExecutionTimeByDataSource = dataSourceExecutionTimes
        };

        // Log manual query execution for full query preview
        // For multi-step queries, we log the final query if present, or concatenated step queries
        var loggedQuery = !string.IsNullOrEmpty(finalQuery)
            ? finalQuery
            : string.Join("; ", stepResults.Select(s => s.SqlQuery));

        var totalResultCount = finalResult?.TotalRecords ?? stepResults.LastOrDefault()?.TotalRows ?? 0;
        var primaryDataSourceId = query.Steps.FirstOrDefault()?.DataSourceId;

        await queryExecutionLogger.LogQueryExecutionAsync(
            queryText: loggedQuery,
            resultCount: totalResultCount,
            executionTimeMs: totalExecutionTime,
            success: allStepsSucceeded,
            dataSourceId: primaryDataSourceId,
            executionContext: "FullQueryPreview",
            errorMessage: executionResult.ErrorMessage,
            userId: userContext.UserId,
            cancellationToken: cancellationToken);

        return executionResult;
    }

    /// <summary>How a step's rows are PII-masked: by the SQL's PII columns always, by result-column names and aliases when
    /// <paramref name="ByResultColumns"/> (the rows delivered as the result).</summary>
    private sealed record StepMasking(McpSettingsData Settings, bool ByResultColumns);

    /// <summary>
    /// Runs one step through <see cref="GatedSqlRunner"/>: the gate judges the text that runs (placeholders bound — values
    /// travel as parameters, never in the text — and flattened), caps it at the row ceiling + 1 rows in the SQL
    /// (<paramref name="rowCeiling"/>, else <see cref="ExecutionRowCeiling"/>), and the provider runs it on its read-only
    /// path. More rows than the ceiling fail the step instead of feeding a silently incomplete result. Also re-checks steps
    /// persisted before the save check existed (§1.5).
    /// </summary>
    private async Task<(QueryStepResult Result, GatedSqlRun Run)> ExecuteStep(
        QueryStep step,
        List<ParameterValue>? parameters,
        StepMasking? masking,
        TimeSpan? statementTimeout,
        CancellationToken cancellationToken,
        int? rowCeiling = null)
    {
        var ceiling = rowCeiling ?? ExecutionRowCeiling;
        if (!step.DataSource.DatabaseEngineType.HasValue)
            throw new BeaconException($"Data source {step.DataSourceId} is not a database type");

        var stepParameters = ExtractStepParameters(step, parameters);
        var (parameterizedSql, sqlParameters) = QueryHelper.PrepareParameterizedQuery(step.SqlValue, stepParameters);

        var run = await sqlRunner.RunAsync(
            new GatedSqlRequest(step.DataSource, StoredSql.Flatten(parameterizedSql, DataSourceSqlDialect.Of(step.DataSource)), sqlParameters, ceiling + 1, masking?.Settings, statementTimeout),
            cancellationToken);

        var errorMessage = run.TimedOut
            ? "Step execution timed out"
            : run.Rows.Count > ceiling
                ? $"Step {step.StepOrder} returned more than {ceiling} rows (the {RowLimitSetting(rowCeiling)} limit); filter or aggregate it so the result fits."
                : null;

        var rows = errorMessage != null ? [] : run.Rows;
        if (masking != null && rows.Count > 0)
        {
            // Mask before the rows leave the step (§1.6/§1.11): the SQL-text matches and host-masked columns always, the
            // result-column names and aliases on the delivered result.
            rows = PiiRowMasker.Mask(
                guardrail,
                rows,
                run.Report.PiiColumns,
                masking.ByResultColumns && masking.Settings.EnablePiiDetection,
                CustomPatterns(masking.Settings),
                run.Report.FinalSql,
                run.Dialect);
        }

        var results = rows
            .Select(x => (IDictionary<string, object?>)x)
            .ToList();

        var stepResult = new QueryStepResult
        {
            StepOrder = step.StepOrder,
            StepName = step.Name ?? $"Step {step.StepOrder}",
            SqlQuery = parameterizedSql,
            DataSourceName = step.DataSource.Name,
            DatabaseEngine = step.DataSource.DatabaseEngineType.Value.ToString(),
            DatabaseEngineType = step.DataSource.DatabaseEngineType.Value,
            PreviewResults = results.Take(10).ToList(),
            AllResults = results,
            TotalRows = results.Count,
            ExecutionTimeMs = run.ExecutionTimeMs,
            Success = errorMessage == null,
            ErrorMessage = errorMessage
        };

        // Log manual query execution for step preview
        await queryExecutionLogger.LogQueryExecutionAsync(
            queryText: parameterizedSql,
            resultCount: results.Count,
            executionTimeMs: run.ExecutionTimeMs,
            success: errorMessage == null,
            dataSourceId: step.DataSourceId,
            executionContext: "QueryStepPreview",
            errorMessage: errorMessage,
            userId: userContext.UserId,
            cancellationToken: CancellationToken.None);

        return (stepResult, run);
    }

    /// <summary>
    /// The final query on the in-memory SQLite store, behind the same gate (regex guardrail, read-only AST, the row cap) as
    /// the steps. A statement that times out, and a result above the ceiling, fail the run — a timed-out statement has no
    /// rows, and an empty result would read as a success to a subscription, a preview or a migration about to write; with
    /// <paramref name="pii"/> the rows are masked by the steps' PII columns read back by name, the final SQL's own matches,
    /// PII-named result columns and its aliases.
    /// </summary>
    private async Task<QueryResult> ExecuteFinalQuery(
        string finalQuery,
        VirtualTableManager virtualTableManager,
        McpSettingsData? pii,
        IReadOnlySet<string> joinPiiColumns,
        CancellationToken cancellationToken,
        int? rowCeiling = null)
    {
        var ceiling = rowCeiling ?? ExecutionRowCeiling;
        logger.LogInformation("Using in-memory SQLite database for final query execution");
        var inMemoryDbLogger = loggerFactory.CreateLogger<InMemoryDatabaseManager>();
        var result = await virtualTableManager.ExecuteFinalQueryWithInMemoryDatabase(
            finalQuery,
            x => gate.Evaluate(JoinGateRequest(x, pii, rowCeiling)),
            inMemoryDbLogger,
            cancellationToken);

        if (result.TimedOut)
        {
            throw new BeaconException(FinalQueryTimedOutMessage);
        }

        if (result.AllRecords.Count > ceiling)
        {
            throw new BeaconException($"The final query returned more than {ceiling} rows (the {RowLimitSetting(rowCeiling)} limit); filter or aggregate it so the result fits.");
        }

        if (pii == null || result.AllRecords.Count == 0)
        {
            return result;
        }

        var report = gate.Evaluate(JoinGateRequest(InMemoryDatabaseManager.TranslateResultReferences(finalQuery), pii, rowCeiling));
        var masked = PiiRowMasker.Mask(
                guardrail,
                result.AllRecords
                    .Select(x => new Dictionary<string, object?>(x))
                    .ToList(),
                report.PiiColumns.Concat(joinPiiColumns),
                pii.EnablePiiDetection,
                CustomPatterns(pii),
                report.FinalSql,
                nameof(DatabaseEngineType.SQLite))
            .Select(x => (IDictionary<string, object?>)x)
            .ToList();

        result.AllRecords = masked;
        result.TopRecords = masked
            .Take(result.TopRecords.Count)
            .ToList();

        return result;
    }

    private static IReadOnlyList<string>? CustomPatterns(McpSettingsData settings) =>
        settings.CustomPiiPatterns.Count > 0 ? settings.CustomPiiPatterns : null;

    private QueryResult ConvertStepToQueryResult(QueryStepResult stepResult, Query query)
    {
        return new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(stepResult.PreviewResults),
            TotalRecords = stepResult.TotalRows,
            DataSourceName = stepResult.DataSourceName,
            SqlQuery = stepResult.SqlQuery,
            AllRecords = stepResult.AllResults,
            TopRecords = stepResult.PreviewResults,
            SubscriptionName = query.Name,
            SubscriptionId = null,
            ExecutionTimeMs = stepResult.ExecutionTimeMs,
            TimedOut = !stepResult.Success,
            Recipients = new List<RecipientData>()
        };
    }

    private List<SubscriptionParameterData> ExtractStepParameters(QueryStep step, List<ParameterValue>? parameters)
    {
        if (parameters == null || !parameters.Any())
            return new List<SubscriptionParameterData>();

        return step.Parameters.Select(p =>
        {
            var value = parameters.FirstOrDefault(param => param.Name == p.Name)?.Value ?? "";
            return new SubscriptionParameterData
            {
                QueryPlaceholder = p.Placeholder,
                Value = value
            };
        }).ToList();
    }

    // The setting that sets a run's row limit, for an over-limit message: the general cap, or a migration's own budget
    // (the only caller that passes its own ceiling).
    private static string RowLimitSetting(int? rowCeiling)
    {
        return rowCeiling == null
            ? $"{QueryExecutionOptions.SectionName}:{nameof(QueryExecutionOptions.MaxExecutionRows)}"
            : $"{MigrationOptions.SectionName}:{nameof(MigrationOptions.MaxRows)}";
    }
}
