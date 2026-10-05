using Beacon.Core.Helpers;
using Microsoft.Extensions.Logging;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Services;

public interface IQueryExecutionPreviewService
{
    /// <summary>One page of a saved query's result; a failure comes back as the result, with its cause.</summary>
    Task<QueryPreviewResult?> ExecuteQueryPreview(int queryId, ListRequest paging, CancellationToken cancellationToken);
    /// <summary>One page of a single step's result, run on its own.</summary>
    Task<QueryPreviewResult?> ExecuteStepPreview(int queryId, int stepOrder, List<ParameterValue>? parameters, ListRequest paging, CancellationToken cancellationToken);
    Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken, List<ParameterValue>? parameters = null);
    Task<QueryStepResult?> ExecuteTemporaryStepPreview(QueryData queryData, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken);
}

internal sealed class QueryExecutionPreviewService : IQueryExecutionPreviewService
{
    private readonly IQueryService _queryService;
    private readonly ILogger<QueryExecutionPreviewService> _logger;

    public QueryExecutionPreviewService(IQueryService queryService, ILogger<QueryExecutionPreviewService> logger)
    {
        _queryService = queryService;
        _logger = logger;
    }

    public async Task<QueryPreviewResult?> ExecuteQueryPreview(int queryId, ListRequest paging, CancellationToken cancellationToken)
    {
        try
        {
            return await _queryService.PreviewQuery(queryId, paging, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Query preview for query {QueryId} was cancelled", queryId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error executing query preview for query {QueryId}", queryId);
            return Failed(ex);
        }
    }

    public async Task<QueryPreviewResult?> ExecuteStepPreview(
        int queryId,
        int stepOrder,
        List<ParameterValue>? parameters,
        ListRequest paging,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _queryService.PreviewQueryStepPaged(queryId, stepOrder, parameters, paging, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Step preview for query {QueryId} step {StepOrder} was cancelled", queryId, stepOrder);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error executing step preview for query {QueryId} step {StepOrder}", queryId, stepOrder);
            return Failed(ex);
        }
    }

    public async Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken, List<ParameterValue>? parameters = null)
    {
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

            // Find the created query
            var queries = await _queryService.GetQueries(new GetQueriesRequest
            {
                QueryName = tempQueryName
            }, cancellationToken);

            var createdQuery = queries.Items?.OrderByDescending(q => q.CreatedTime).FirstOrDefault();
            if (createdQuery?.QueryId == null)
            {
                return null;
            }

            tempQueryId = createdQuery.QueryId;

            // Execute the query
            return await _queryService.ExecuteQueryAdvanced(createdQuery.QueryId.Value, parameters: parameters, cancellationToken: cancellationToken);
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

    public async Task<QueryStepResult?> ExecuteTemporaryStepPreview(QueryData queryData, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken)
    {
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

            // Find the created query
            var queries = await _queryService.GetQueries(new GetQueriesRequest
            {
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

    // A preview is run by the user who wrote the query, so the failure goes back to them as the result
    // (the shell renders ErrorMessage) instead of null, which the handlers could only turn into
    // "preview failed" — hiding the cause in a host log the user usually cannot see.
    private static QueryPreviewResult Failed(Exception exception) =>
        new() { Success = false, ErrorMessage = exception.Message };
}
