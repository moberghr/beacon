using Semantico.Core.Models.Queries;

namespace Semantico.Core.Services;

public interface IQueryExecutionPreviewService
{
    Task<QueryExecutionResult?> ExecuteQueryPreview(int queryId, CancellationToken cancellationToken);
    Task<QueryStepResult?> ExecuteStepPreview(int queryId, int stepOrder, CancellationToken cancellationToken);
    Task<QueryStepResult?> ExecuteStepPreview(int queryId, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken);
    Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken);
    Task<QueryStepResult?> ExecuteTemporaryStepPreview(QueryData queryData, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken);
}

internal sealed class QueryExecutionPreviewService : IQueryExecutionPreviewService
{
    private readonly IQueryService _queryService;
    
    public QueryExecutionPreviewService(IQueryService queryService)
    {
        _queryService = queryService;
    }

    public async Task<QueryExecutionResult?> ExecuteQueryPreview(int queryId, CancellationToken cancellationToken)
    {
        try
        {
            return await _queryService.ExecuteQueryAdvanced(queryId, cancellationToken: cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<QueryStepResult?> ExecuteStepPreview(int queryId, int stepOrder, CancellationToken cancellationToken)
    {
        try
        {
            return await _queryService.PreviewQueryStep(queryId, stepOrder, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<QueryStepResult?> ExecuteStepPreview(int queryId, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken)
    {
        try
        {
            return await _queryService.PreviewQueryStep(queryId, stepOrder, parameters, cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    public async Task<QueryExecutionResult?> ExecuteTemporaryQueryPreview(QueryData queryData, CancellationToken cancellationToken)
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
                FinalQueryProjectId = queryData.FinalQueryProjectId
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
            return await _queryService.ExecuteQueryAdvanced(createdQuery.QueryId.Value, cancellationToken: cancellationToken);
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
}