using Beacon.Core.Helpers;
using Microsoft.Extensions.Logging;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Services;

public interface IQueryExecutionPreviewService
{
    /// <summary>
    /// One page of a query's result (the saved query, or <paramref name="draft"/> when given); a failure comes
    /// back as the result, with its cause.
    /// </summary>
    Task<QueryPreviewResult?> ExecuteQueryPreview(int queryId, QueryDraft? draft, ListRequest paging, CancellationToken cancellationToken);
    /// <summary>One page of a single step's result, run on its own; from <paramref name="draft"/> when given.</summary>
    Task<QueryPreviewResult?> ExecuteStepPreview(int queryId, int stepOrder, List<ParameterValue>? parameters, QueryDraft? draft, ListRequest paging, CancellationToken cancellationToken);
    /// <summary>Runs <paramref name="queryData"/> once as a temporary query.</summary>
    Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken, List<ParameterValue>? parameters = null);

    /// <summary>
    /// Runs <paramref name="queryData"/> once as a temporary query (a migration's read); <paramref name="maxRows"/> replaces
    /// <c>Beacon:Query:MaxExecutionRows</c> as the most rows a statement may return.
    /// </summary>
    Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, List<ParameterValue>? parameters, int maxRows, CancellationToken cancellationToken);
    Task<QueryStepResult?> ExecuteTemporaryStepPreview(QueryData queryData, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken);
}

internal sealed class QueryExecutionPreviewService : IQueryExecutionPreviewService
{
    private readonly IQueryService _queryService;
    private readonly IStoredQueryPreview _storedPreview;
    private readonly ProjectRequestAuthorizer _authorizer;
    private readonly ILogger<QueryExecutionPreviewService> _logger;

    public QueryExecutionPreviewService(
        IQueryService queryService,
        IStoredQueryPreview storedPreview,
        ProjectRequestAuthorizer authorizer,
        ILogger<QueryExecutionPreviewService> logger)
    {
        _queryService = queryService;
        _storedPreview = storedPreview;
        _authorizer = authorizer;
        _logger = logger;
    }

    public async Task<QueryPreviewResult?> ExecuteQueryPreview(int queryId, QueryDraft? draft, ListRequest paging, CancellationToken cancellationToken)
    {
        await DemandMayRunAsync(queryId, draft, cancellationToken);

        try
        {
            if (draft != null)
            {
                return await _queryService.PreviewQuery(queryId, draft, paging, cancellationToken);
            }

            var snapshot = await LoadRunnableSnapshotAsync(queryId, cancellationToken);

            return await _storedPreview.PreviewSnapshot(snapshot, paging, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Query preview for query {QueryId} was cancelled", queryId);
            return null;
        }
        catch (Exception ex) when (!IsAccessRefusal(ex))
        {
            // The type and error code only (§1.11): the message can quote the SQL or row values.
            _logger.LogWarning(
                "Error executing query preview for query {QueryId}: {Failure}",
                queryId,
                FailureCodes.Describe(ex));
            return Failed(ex);
        }
    }

    public async Task<QueryPreviewResult?> ExecuteStepPreview(
        int queryId,
        int stepOrder,
        List<ParameterValue>? parameters,
        QueryDraft? draft,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        await DemandMayRunAsync(queryId, draft, cancellationToken);

        try
        {
            if (draft != null)
            {
                return await _queryService.PreviewQueryStepPaged(queryId, stepOrder, parameters, draft, paging, cancellationToken);
            }

            var snapshot = await LoadRunnableSnapshotAsync(queryId, cancellationToken);

            return await _storedPreview.PreviewSnapshotStep(snapshot, stepOrder, parameters, paging, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Step preview for query {QueryId} step {StepOrder} was cancelled", queryId, stepOrder);
            return null;
        }
        catch (Exception ex) when (!IsAccessRefusal(ex))
        {
            _logger.LogWarning(
                "Error executing step preview for query {QueryId} step {StepOrder}: {Failure}",
                queryId,
                stepOrder,
                FailureCodes.Describe(ex));
            return Failed(ex);
        }
    }

    public Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken, List<ParameterValue>? parameters = null)
    {
        return ExecuteTemporaryQueryPreviewCore(queryData, parameters, null, cancellationToken);
    }

    public Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, List<ParameterValue>? parameters, int maxRows, CancellationToken cancellationToken)
    {
        return ExecuteTemporaryQueryPreviewCore(queryData, parameters, maxRows, cancellationToken);
    }

    public async Task<QueryStepResult?> ExecuteTemporaryStepPreview(QueryData queryData, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken)
    {
        await DemandMayQueryEachStepAsync(
            queryData.Steps
                .Where(x => x.StepOrder <= stepOrder)
                .ToList(),
            null,
            cancellationToken);

        int? tempQueryId = null;
        try
        {
            // Create temporary query with steps up to the target step
            var tempQueryName = $"TEMP_STEP_PREVIEW_{Guid.NewGuid():N}";
            var tempQuery = new QueryData
            {
                Name = tempQueryName,
                Description = "Temporary query for step preview",
                Steps = queryData.Steps.Where(s => s.StepOrder <= stepOrder).ToList()
            };

            var createResponse = await _queryService.CreateQuery(tempQuery, cancellationToken);
            if (!createResponse.Success)
            {
                return null;
            }

            // Find the created query: a lookup of the row this method just created, not a listing for the caller.
            var queries = await _queryService.GetQueries(new GetQueriesRequest
            {
                Readable = ProjectSet.All,
                QueryName = tempQueryName
            }, cancellationToken);

            var createdQuery = queries.Items?.OrderByDescending(q => q.CreatedTime).FirstOrDefault();
            if (createdQuery?.QueryId == null)
            {
                return null;
            }

            tempQueryId = createdQuery.QueryId;

            // Execute the step with parameters
            return await _queryService.PreviewQueryStep(createdQuery.QueryId.Value, stepOrder, parameters, cancellationToken);
        }
        finally
        {
            // Clean up: delete the temporary query
            if (tempQueryId.HasValue)
            {
                try
                {
                    await _queryService.DeleteQuery(tempQueryId.Value, cancellationToken);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    private async Task<QueryExecutionResult?> ExecuteTemporaryQueryPreviewCore(QueryData queryData, List<ParameterValue>? parameters, int? maxRows, CancellationToken cancellationToken)
    {
        await DemandMayQueryEachStepAsync(queryData.Steps, queryData.FinalQueryDataSourceId, cancellationToken);

        int? tempQueryId = null;
        try
        {
            // Create temporary query
            var tempQueryName = $"TEMP_PREVIEW_{Guid.NewGuid():N}";
            var tempQuery = new QueryData
            {
                Name = tempQueryName,
                Description = "Temporary query for preview",
                Steps = queryData.Steps,
                FinalQuery = queryData.FinalQuery,
                FinalQueryDataSourceId = queryData.FinalQueryDataSourceId
            };

            var createResponse = await _queryService.CreateQuery(tempQuery, cancellationToken);
            if (!createResponse.Success)
            {
                return null;
            }

            // Find the created query: a lookup of the row this method just created, not a listing for the caller.
            var queries = await _queryService.GetQueries(new GetQueriesRequest
            {
                Readable = ProjectSet.All,
                QueryName = tempQueryName
            }, cancellationToken);

            var createdQuery = queries.Items?.OrderByDescending(q => q.CreatedTime).FirstOrDefault();
            if (createdQuery?.QueryId == null)
            {
                return null;
            }

            tempQueryId = createdQuery.QueryId;

            // Execute the query, within the caller's own row limit when it has one
            return maxRows is { } limit
                ? await _queryService.ExecuteQueryAdvanced(createdQuery.QueryId.Value, null, parameters, limit, cancellationToken)
                : await _queryService.ExecuteQueryAdvanced(createdQuery.QueryId.Value, parameters: parameters, cancellationToken: cancellationToken);
        }
        finally
        {
            // Clean up: delete the temporary query
            if (tempQueryId.HasValue)
            {
                try
                {
                    await _queryService.DeleteQuery(tempQueryId.Value, cancellationToken);
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }
    }

    // Checked before anything is read, outside the catch that turns failures into results: the stored query needs Run
    // (unreadable or missing is 404, a query without steps is its owner's), a draft's sources need Query (one project
    // holding all of them, or all openly reachable). A draft runs exactly the steps it carries.
    private Task DemandMayRunAsync(int queryId, QueryDraft? draft, CancellationToken cancellationToken)
    {
        var check = draft == null
            ? new ProjectCheck(ProjectResource.Query(queryId), ProjectAction.Run)
            : new ProjectCheck(ProjectResource.DataSources(draft.Steps?.Select(x => x.DataSourceId)), ProjectAction.Query);

        return _authorizer.DemandAsync(check, cancellationToken);
    }

    // Temporary steps are ad-hoc SQL with no saved query behind them: a caller other than system work (which, like an
    // Admin, is not checked) needs Query on each step's data source, each on its own. A step naming no data source is
    // refused as missing.
    private Task DemandMayQueryEachStepAsync(IEnumerable<QueryStepData> steps, int? finalQueryDataSourceId, CancellationToken cancellationToken)
    {
        var checks = steps
            .Select(x => x.DataSourceId)
            .Concat(finalQueryDataSourceId is { } id ? [id] : [])
            .Distinct()
            .Select(x => new ProjectCheck(ProjectResource.DataSource(x), ProjectAction.Query))
            .ToList();

        return _authorizer.DemandAsync(checks, cancellationToken);
    }

    // The stored steps that run are the ones authorized here, right before their SQL: the query is read once, detached,
    // and the data sources of that snapshot need Run in one project holding all of them (or all openly reachable), so an
    // edit saved after the check above cannot run a step on a source the caller may not run. A snapshot without steps
    // runs no source SQL and needs nothing beyond that check.
    private async Task<StoredQuerySnapshot> LoadRunnableSnapshotAsync(int queryId, CancellationToken cancellationToken)
    {
        var snapshot = await _storedPreview.LoadSnapshot(queryId, cancellationToken);
        if (snapshot.Steps.Count > 0)
        {
            await _authorizer.DemandAsync(new ProjectCheck(ProjectResource.DataSources(snapshot.DataSourceIds), ProjectAction.Run), cancellationToken);
        }

        return snapshot;
    }

    // A refusal is the answer (404 or 403, already logged), never a failed preview.
    private static bool IsAccessRefusal(Exception exception) =>
        exception is BeaconNotFoundException or BeaconForbiddenException;

    // A preview is run by the user who wrote the query, so the failure goes back to them as the result
    // (the shell renders ErrorMessage) instead of null, which the handlers could only turn into
    // "preview failed" — hiding the cause in a host log the user usually cannot see.
    private static QueryPreviewResult Failed(Exception exception) =>
        new() { Success = false, ErrorMessage = exception.Message };
}
