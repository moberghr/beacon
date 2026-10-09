using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Validators;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

/// <summary>
/// Paged previews for the query screens. Subscriptions keep <see cref="ExecuteQueryAdvanced"/> — they need
/// every row — while a preview reads one page and a count. Intermediate steps of a multi-step query still
/// run in full, because the final query joins them; only their first rows travel to the client.
/// </summary>
internal partial class QueryService
{
    private const int IntermediatePreviewRows = 10;

    public async Task<QueryPreviewResult> PreviewQuery(int queryId, QueryDraft? draft, ListRequest paging, CancellationToken cancellationToken)
    {
        List<QueryStep> steps;
        string? finalQuery;
        if (draft == null)
        {
            var query = await GetQueryWithSteps(queryId, cancellationToken);
            steps = query.Steps
                .OrderBy(x => x.StepOrder)
                .ToList();
            finalQuery = query.FinalQuery;
        }
        else
        {
            steps = await BuildDraftSteps(queryId, draft.Steps, cancellationToken);
            finalQuery = draft.FinalQuery;
        }

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
            cancellationToken: cancellationToken);

        return result;
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

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var step = await context.QuerySteps
            .Include(x => x.DataSource)
            .Include(x => x.Parameters)
            .Where(x => x.QueryId == queryId)
            .Where(x => x.StepOrder == stepOrder)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query #{queryId} has no step {stepOrder}.");

        return await PreviewSingleStep(step, parameters, paging, cancellationToken);
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
            QueryValidator.CheckForFlaggedWords(x.SqlValue);
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
        using var virtualTableManager = new VirtualTableManager(loggerFactory.CreateLogger<VirtualTableManager>());
        var summaries = new List<QueryPreviewStep>();
        var totalExecutionTime = 0.0;

        foreach (var step in steps)
        {
            var stepResult = await ExecuteStep(step, null);
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
            var page = await virtualTableManager.ExecuteFinalQueryPagedAsync(
                finalQuery,
                readOnlyAstValidator,
                loggerFactory.CreateLogger<InMemoryDatabaseManager>(),
                paging,
                cancellationToken);
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
    /// <see cref="ExecuteStep"/>'s gates (read-only AST validation, host policy, masking) around one page. The
    /// rewritten page and count statements are re-validated too; a rejected rewrite falls back to streaming
    /// the original, already-approved statement.
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

        var engine = step.DataSource.DatabaseEngineType.Value;
        var dialect = engine.ToString();
        var (parameterizedSql, sqlParameters) = QueryHelper.PrepareParameterizedQuery(step.SqlValue, ExtractStepParameters(step, parameters));
        var executedSql = FlattenSql(parameterizedSql);

        var rejection = readOnlyAstValidator.Validate(executedSql, dialect);
        if (rejection != null)
        {
            throw new InvalidOperationException(rejection);
        }

        var hostCheck = hostGuard.Check(step.DataSource, executedSql);
        var hostError = hostCheck.Allowed ? hostCheck.FindUnboundParameter(sqlParameters) : hostCheck.Error;
        if (hostError != null)
        {
            throw new InvalidOperationException(hostError);
        }

        // Ordering by a masked column would leak the order of the values it hides.
        var sort = paging.SortCriteria().FirstOrDefault();
        if (sort != null && hostCheck.MaskedOutputColumns.Contains(sort.SortColumn, StringComparer.OrdinalIgnoreCase))
        {
            paging = new PreviewPaging { Page = paging.Page, PageSize = paging.PageSize };
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var connection = DbConnectionFactory.CreateConnection(engine, connectionResolver.GetConnectionString(step.DataSource));
        await connection.OpenAsync(cancellationToken);

        SqlResultPage page;
        try
        {
            page = await SqlPageExecutor.ExecuteAsync(
                connection,
                executedSql,
                sqlParameters,
                dialect,
                paging,
                x => readOnlyAstValidator.Validate(x, dialect) == null && hostGuard.Check(step.DataSource, x).Allowed,
                null,
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            stopwatch.Stop();
            return (StepSummary(step, false, ex.Message, stopwatch.Elapsed.TotalMilliseconds, 0, []), null);
        }

        stopwatch.Stop();

        if (hostCheck.MaskedOutputColumns.Count > 0)
        {
            page = page with
            {
                Rows = hostGuard.Mask(
                        page.Rows
                            .Select(x => new Dictionary<string, object?>(x))
                            .ToList(),
                        hostCheck.MaskedOutputColumns)
                    .Select(x => (IDictionary<string, object?>)x)
                    .ToList()
            };
        }

        return (StepSummary(step, true, null, stopwatch.Elapsed.TotalMilliseconds, page.TotalCount, page.Rows), page);
    }

    private static QueryPreviewStep StepSummary(
        QueryStep step,
        bool success,
        string? errorMessage,
        double executionTimeMs,
        int totalRows,
        List<IDictionary<string, object?>> previewRows) =>
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

    private static QueryPreviewStep ToSummary(QueryStepResult result, List<IDictionary<string, object?>> previewRows) =>
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
