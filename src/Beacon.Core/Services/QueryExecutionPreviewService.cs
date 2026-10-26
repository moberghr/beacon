using Beacon.Core.Models.Queries;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Services;

internal sealed class QueryExecutionPreviewService
{
    private readonly IStoredQueryPreview _storedPreview;
    private readonly ProjectRequestAuthorizer _authorizer;

    public QueryExecutionPreviewService(
        IStoredQueryPreview storedPreview,
        ProjectRequestAuthorizer authorizer)
    {
        _storedPreview = storedPreview;
        _authorizer = authorizer;
    }

    /// <summary>Runs <paramref name="queryData"/>'s steps once, in-process; no query is stored to run them.</summary>
    public Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken, List<ParameterValue>? parameters = null)
    {
        return ExecuteTemporaryQueryPreviewCore(queryData, parameters, null, cancellationToken);
    }

    /// <summary>
    /// Runs <paramref name="queryData"/>'s steps once, in-process (a migration's read), storing no query;
    /// <paramref name="maxRows"/> replaces <c>Beacon:Query:MaxExecutionRows</c> as the most rows a statement may return.
    /// </summary>
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
}
