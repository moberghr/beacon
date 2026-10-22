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
    /// <summary>Runs <paramref name="queryData"/>'s steps once, in-process; no query is stored to run them.</summary>
    Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken, List<ParameterValue>? parameters = null);

    /// <summary>
    /// Runs <paramref name="queryData"/>'s steps once, in-process (a migration's read), storing no query;
    /// <paramref name="maxRows"/> replaces <c>Beacon:Query:MaxExecutionRows</c> as the most rows a statement may return.
    /// </summary>
    Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, List<ParameterValue>? parameters, int maxRows, CancellationToken cancellationToken);
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

    // The steps run are a detached copy taken before anything is checked: the legacy single-step form (no steps, the SQL and
    // data source on the query itself) becomes its one step first, and exactly the copy's steps are authorized, validated
    // and run. Nothing is stored to run them, so nothing is left behind if the run is cancelled, and nothing the caller
    // changes after the check reaches the run.
    private async Task<QueryExecutionResult?> ExecuteTemporaryQueryPreviewCore(QueryData queryData, List<ParameterValue>? parameters, int? maxRows, CancellationToken cancellationToken)
    {
        var snapshot = Snapshot(queryData);
        await DemandMayQueryEachStepAsync(snapshot.Steps, snapshot.FinalQueryDataSourceId, cancellationToken);

        return await _storedPreview.ExecuteTemporaryQuery(snapshot, parameters, maxRows, cancellationToken);
    }

    // A copy of the query's steps (each with its parameters) and final query; never empty: a query without steps is its
    // legacy single step, from the SQL, data source and parameters it carries.
    private static QueryData Snapshot(QueryData queryData)
    {
        var steps = queryData.Steps.Count > 0
            ? queryData.Steps
            :
            [
                new QueryStepData
                {
                    StepOrder = 1,
                    Name = "Step 1",
                    SqlValue = queryData.SqlValue,
                    DataSourceId = queryData.DataSourceId,
                    DataSourceName = queryData.DataSourceName,
                    Parameters = queryData.Parameters
                        .Select(x =>
                            new QueryStepParameterData
                            {
                                Name = x.Name,
                                Type = x.Type,
                                Description = x.Description,
                                Placeholder = x.Placeholder
                            })
                        .ToList()
                }
            ];

        return new QueryData
        {
            Name = queryData.Name,
            Description = queryData.Description,
            FinalQuery = queryData.FinalQuery,
            FinalQueryDataSourceId = queryData.FinalQueryDataSourceId,
            Steps = steps
                .Select(x =>
                    new QueryStepData
                    {
                        StepOrder = x.StepOrder,
                        Name = x.Name,
                        Description = x.Description,
                        SqlValue = x.SqlValue,
                        DataSourceId = x.DataSourceId,
                        DataSourceName = x.DataSourceName,
                        DataSourceType = x.DataSourceType,
                        DatabaseEngineType = x.DatabaseEngineType,
                        Parameters = x.Parameters
                            .Select(y =>
                                new QueryStepParameterData
                                {
                                    Name = y.Name,
                                    Type = y.Type,
                                    Description = y.Description,
                                    Placeholder = y.Placeholder
                                })
                            .ToList()
                    })
                .ToList()
        };
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
