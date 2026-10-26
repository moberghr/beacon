using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services.Validation;
using Beacon.Core.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

/// <summary>
/// Paged previews for the query screens. Subscriptions keep <see cref="ExecuteQueryAdvanced"/> — they need
/// every row — while a preview reads one page and a count. Intermediate steps of a multi-step query still
/// run in full, because the final query joins them; only their first rows travel to the client.
/// </summary>
internal partial class QueryService : IStoredQueryPreview
{
    private const int IntermediatePreviewRows = 10;

    public async Task<QueryPreviewResult> PreviewQuery(int queryId, QueryDraft? draft, ListRequest paging, CancellationToken cancellationToken)
    {
        if (draft == null)
        {
            var snapshot = await LoadSnapshot(queryId, cancellationToken);

            return await PreviewSnapshot(snapshot, paging, cancellationToken);
        }

        var steps = await BuildDraftSteps(queryId, draft.Steps, cancellationToken);

        return await PreviewSteps(steps, draft.FinalQuery, paging, cancellationToken);
    }

    public async Task<QueryPreviewResult> PreviewQueryStepPaged(
        int queryId,
        int stepOrder,
        List<ParameterValue>? parameters,
        QueryDraft? draft,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        if (draft != null)
        {
            var draftStep = draft.Steps
                .Where(x => x.StepOrder == stepOrder)
                .ToList();

            if (draftStep.Count == 0)
            {
                throw new InvalidOperationException($"The draft of query #{queryId} has no step {stepOrder}.");
            }

            var steps = await BuildDraftSteps(queryId, draftStep, cancellationToken);
            return await PreviewSingleStep(steps[0], parameters, paging, cancellationToken);
        }

        var snapshot = await LoadSnapshot(queryId, cancellationToken);

        return await PreviewSnapshotStep(snapshot, stepOrder, parameters, paging, cancellationToken);
    }

    public async Task<StoredQuerySnapshot> LoadSnapshot(int queryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .AsNoTracking()
            .Include(x => x.Steps)
                .ThenInclude(x => x.DataSource)
            .Include(x => x.Steps)
                .ThenInclude(x => x.Parameters)
            .Where(x => x.Id == queryId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query #{queryId} not found.");

        return new StoredQuerySnapshot
        {
            QueryId = query.Id,
            Steps = query.Steps
                .OrderBy(x => x.StepOrder)
                .ToList(),
            FinalQuery = query.FinalQuery
        };
    }

    public Task<QueryPreviewResult> PreviewSnapshot(StoredQuerySnapshot snapshot, ListRequest paging, CancellationToken cancellationToken)
    {
        return PreviewSteps(snapshot.Steps.ToList(), snapshot.FinalQuery, paging, cancellationToken);
    }

    public Task<QueryPreviewResult> PreviewSnapshotStep(
        StoredQuerySnapshot snapshot,
        int stepOrder,
        List<ParameterValue>? parameters,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        var step = snapshot.Steps
            .Where(x => x.StepOrder == stepOrder)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Query #{snapshot.QueryId} has no step {stepOrder}.");

        return PreviewSingleStep(step, parameters, paging, cancellationToken);
    }

    private async Task<QueryPreviewResult> PreviewSteps(
        List<QueryStep> steps,
        string? finalQuery,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        QueryPreviewResult result;
        if (steps.Count == 1 && string.IsNullOrEmpty(finalQuery))
        {
            result = await PreviewSingleStep(steps[0], null, paging, cancellationToken);
        }
        else
        {
            result = await PreviewMultiStep(steps, finalQuery, paging, cancellationToken);
        }

        await queryExecutionLogger.LogQueryExecutionAsync(
            queryText: !string.IsNullOrEmpty(finalQuery)
                ? finalQuery
                : string.Join("; ", steps.Select(x => x.SqlValue)),
            resultCount: result.Result?.TotalCount ?? result.Steps.LastOrDefault()?.TotalRows ?? 0,
            executionTimeMs: result.TotalExecutionTimeMs,
            success: result.Success,
            dataSourceId: steps.FirstOrDefault()?.DataSourceId,
            executionContext: "FullQueryPreview",
            errorMessage: result.ErrorMessage,
            userId: userContext.UserId,
            cancellationToken: CancellationToken.None);

        return result;
    }

    /// <summary>
    /// The editor's unsaved steps as detached entities, checked the way <see cref="UpdateQuery"/> checks a
    /// save. They are never attached to a context, so running a draft cannot persist it; execution then goes
    /// through the same read-only, host-policy and masking gates as a saved step.
    /// </summary>
    private async Task<List<QueryStep>> BuildDraftSteps(int queryId, List<QueryStepData> stepData, CancellationToken cancellationToken)
    {
        foreach (var x in stepData)
        {
            QueryValidator.CheckForFlaggedWords(x.SqlValue, readOnlyAstValidator.MaxSqlChars);
        }

        var dataSourceIds = stepData
            .Select(x => x.DataSourceId)
            .Distinct()
            .ToList();

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSources = await context.DataSources
            .AsNoTracking()
            .Where(x => dataSourceIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var steps = new List<QueryStep>();
        foreach (var x in stepData.OrderBy(y => y.StepOrder))
        {
            if (!dataSources.TryGetValue(x.DataSourceId, out var dataSource))
            {
                throw new InvalidOperationException($"Data source {x.DataSourceId} not found.");
            }

            steps.Add(new QueryStep
            {
                QueryId = queryId,
                DataSourceId = x.DataSourceId,
                DataSource = dataSource,
                StepOrder = x.StepOrder,
                Name = x.Name,
                Description = x.Description,
                SqlValue = x.SqlValue,
                Parameters = ParameterEntityFactory.CreateQueryStepParameters(x.Parameters, 0),
            });
        }

        return steps;
    }

    private async Task<QueryPreviewResult> PreviewSingleStep(
        QueryStep step,
        List<ParameterValue>? parameters,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        var (summary, page) = await ExecuteStepPaged(step, parameters, paging, cancellationToken);

        return new QueryPreviewResult
        {
            Success = summary.Success,
            ErrorMessage = summary.ErrorMessage,
            TotalExecutionTimeMs = summary.ExecutionTimeMs,
            DataSourcesInvolved = [summary.DataSourceName],
            Steps = [summary],
            Result = page == null ? null : ToResultPage(page, paging),
        };
    }

    private async Task<QueryPreviewResult> PreviewMultiStep(
        List<QueryStep> steps,
        string? finalQuery,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        using var virtualTableManager = new VirtualTableManager(loggerFactory.CreateLogger<VirtualTableManager>())
        {
            StatementTimeoutSeconds = FinalQueryTimeoutSeconds
        };
        var summaries = new List<QueryPreviewStep>();
        var totalExecutionTime = 0.0;

        foreach (var step in steps)
        {
            var (stepResult, _) = await ExecuteStep(step, null, null, null, cancellationToken);
            totalExecutionTime += stepResult.ExecutionTimeMs;
            summaries.Add(ToSummary(stepResult, stepResult.PreviewResults.Take(IntermediatePreviewRows).ToList()));

            if (!stepResult.Success)
            {
                break;
            }

            virtualTableManager.AddVirtualTable(
                $"@result{step.StepOrder}",
                stepResult.AllResults,
                new ProjectInfo
                {
                    Name = step.DataSource.Name,
                    DatabaseEngine = step.DataSource.DatabaseEngineType?.ToString() ?? step.DataSource.DataSourceType.ToString(),
                    DatabaseEngineType = step.DataSource.DatabaseEngineType ?? Data.Enums.DatabaseEngineType.PostgreSQL
                });
        }

        var failed = summaries.FirstOrDefault(x => !x.Success);
        QueryResultPage? result = null;
        if (failed == null && !string.IsNullOrEmpty(finalQuery))
        {
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            SqlResultPage page;
            try
            {
                page = await virtualTableManager.ExecuteFinalQueryPagedAsync(
                    finalQuery,
                    x => gate.Evaluate(JoinGateRequest(x, null) with { MaxRows = null }),
                    loggerFactory.CreateLogger<InMemoryDatabaseManager>(),
                    paging,
                    cancellationToken);
            }
            catch (InMemoryJoinTimeoutException)
            {
                throw new BeaconException(FinalQueryTimedOutMessage);
            }

            totalExecutionTime += stopwatch.Elapsed.TotalMilliseconds;
            result = ToResultPage(page, paging);
        }

        return new QueryPreviewResult
        {
            Success = failed == null,
            ErrorMessage = failed?.ErrorMessage,
            TotalExecutionTimeMs = totalExecutionTime,
            DataSourcesInvolved = summaries
                .Select(x => x.DataSourceName)
                .Distinct()
                .ToList(),
            Steps = summaries,
            Result = result,
        };
    }

    /// <summary>
    /// One page of a step, every statement through <see cref="GatedSqlRunner"/> (the execution gate, then the provider's
    /// read-only path): the page and count rewrites when the SQL shape allows them and they pass the gate too; otherwise
    /// the original statement capped at <see cref="ExecutionRowCeiling"/> + 1 rows in the SQL, paged in memory — a result
    /// above the ceiling then cannot be previewed. The original must pass the gate before anything runs.
    /// </summary>
    private async Task<(QueryPreviewStep Summary, SqlResultPage? Page)> ExecuteStepPaged(
        QueryStep step,
        List<ParameterValue>? parameters,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        if (!step.DataSource.DatabaseEngineType.HasValue)
        {
            throw new BeaconException($"Data source {step.DataSourceId} is not a database type");
        }

        var dialect = DataSourceSqlDialect.Of(step.DataSource);
        var (parameterizedSql, sqlParameters) = QueryHelper.PrepareParameterizedQuery(step.SqlValue, ExtractStepParameters(step, parameters));
        var executedSql = StoredSql.Flatten(parameterizedSql, dialect);
        var rowSecurity = await rowSecurityResolver.ResolveAsync(step.DataSourceId, cancellationToken);

        var original = new GatedSqlRequest(step.DataSource, executedSql, sqlParameters, ExecutionRowCeiling + 1, rowSecurity, ExecutionContext: SqlExecutionContexts.QueryStepPreview);
        var report = sqlRunner.Evaluate(original);
        if (report.Blocked)
        {
            throw new InvalidOperationException(report.BlockReason ?? "The SQL was rejected.");
        }

        // Ordering by a masked column would leak the order of the values it hides; on this path the gate reports only the
        // host data source's masked columns.
        var sort = paging.SortCriteria().FirstOrDefault();
        if (sort != null && report.PiiColumns.Contains(sort.SortColumn, StringComparer.OrdinalIgnoreCase))
        {
            paging = new PreviewPaging { Page = paging.Page, PageSize = paging.PageSize };
            sort = null;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        SqlResultPage page;
        try
        {
            page = await ReadPageAsync(step, original, dialect, paging, sort, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return (StepSummary(step, false, ex.Message, stopwatch.Elapsed.TotalMilliseconds, 0, []), null);
        }

        stopwatch.Stop();

        return (StepSummary(step, true, null, stopwatch.Elapsed.TotalMilliseconds, page.TotalCount, page.Rows), page);
    }

    private async Task<SqlResultPage> ReadPageAsync(
        QueryStep step,
        GatedSqlRequest original,
        string? dialect,
        ListRequest paging,
        SortCriterion? sort,
        CancellationToken cancellationToken)
    {
        var limit = paging.PageSizeOrDefault;
        var offset = paging.PageOrDefault * limit;
        var plan = SqlPageRewriter.Plan(original.Sql, dialect, offset, limit, sort);
        // The page carries its own LIMIT / FETCH of one page and the count returns one row; each is capped one row past
        // that, so the read ends on its own instead of being cancelled at the cap.
        var pageRequest = plan.PageSql == null ? null : original with { Sql = plan.PageSql, MaxRows = limit + 1 };
        var countRequest = plan.CountSql == null ? null : original with { Sql = plan.CountSql, MaxRows = 2 };

        if (pageRequest != null && countRequest != null && !sqlRunner.Evaluate(pageRequest).Blocked && !sqlRunner.Evaluate(countRequest).Blocked)
        {
            var pageRun = await sqlRunner.RunAsync(pageRequest, cancellationToken);
            var countRun = await sqlRunner.RunAsync(countRequest, cancellationToken);
            if (pageRun.TimedOut || countRun.TimedOut)
            {
                throw new TimeoutException("Step execution timed out");
            }

            var count = countRun.Rows.FirstOrDefault()?.Values.FirstOrDefault();

            return new SqlResultPage(
                pageRun.Rows,
                (int)Math.Min(Convert.ToInt64(count ?? 0L), int.MaxValue),
                plan.SortApplied);
        }

        var run = await sqlRunner.RunAsync(original, cancellationToken);
        if (run.TimedOut)
        {
            throw new TimeoutException("Step execution timed out");
        }

        if (run.Rows.Count > ExecutionRowCeiling)
        {
            throw new BeaconException($"Step {step.StepOrder} returned more than {ExecutionRowCeiling} rows (the {RowLimitSetting(null)} limit) and its SQL cannot be paged; filter or aggregate it to preview it.");
        }

        return new SqlResultPage(
            run.Rows
                .Skip(offset)
                .Take(limit)
                .ToList(),
            run.Rows.Count,
            false);
    }

    private static QueryPreviewStep StepSummary(
        QueryStep step,
        bool success,
        string? errorMessage,
        double executionTimeMs,
        int totalRows,
        IReadOnlyList<IDictionary<string, object?>> previewRows) =>
        new()
        {
            StepOrder = step.StepOrder,
            StepName = step.Name ?? $"Step {step.StepOrder}",
            DataSourceName = step.DataSource.Name,
            DatabaseEngine = step.DataSource.DatabaseEngineType?.ToString() ?? step.DataSource.DataSourceType.ToString(),
            Success = success,
            ErrorMessage = errorMessage,
            ExecutionTimeMs = executionTimeMs,
            TotalRows = totalRows,
            PreviewRows = previewRows,
        };

    private static QueryPreviewStep ToSummary(QueryStepResult result, IReadOnlyList<IDictionary<string, object?>> previewRows) =>
        new()
        {
            StepOrder = result.StepOrder,
            StepName = result.StepName,
            DataSourceName = result.DataSourceName,
            DatabaseEngine = result.DatabaseEngine,
            Success = result.Success,
            ErrorMessage = result.ErrorMessage,
            ExecutionTimeMs = result.ExecutionTimeMs,
            TotalRows = result.TotalRows,
            PreviewRows = previewRows,
        };

    private static QueryResultPage ToResultPage(SqlResultPage page, ListRequest paging)
    {
        var pageSize = paging.PageSizeOrDefault;
        var sort = paging.SortCriteria().FirstOrDefault();

        return new QueryResultPage
        {
            Rows = page.Rows,
            TotalCount = page.TotalCount,
            PageCount = page.TotalCount == 0 ? 0 : (int)Math.Ceiling(page.TotalCount / (double)pageSize),
            Page = paging.PageOrDefault,
            PageSize = pageSize,
            Sortable = sort == null || page.SortApplied,
            Sort = page.SortApplied && sort != null
                ? (sort.SortDirection == SortDirection.Descending ? "-" : string.Empty) + sort.SortColumn
                : null,
        };
    }

    private sealed record PreviewPaging : ListRequest;
}
