using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Helpers.File;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Models.Recipients;
using Beacon.Core.Models.Subscriptions;
using Beacon.Core.Validators;

namespace Beacon.Core.Services;

internal partial class QueryService
{

    public async Task<BaseResponse> AddQueryStep(int queryId, QueryStepData stepData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var queryStep = new QueryStep
        {
            QueryId = queryId,
            DataSourceId = stepData.DataSourceId,
            StepOrder = stepData.StepOrder,
            Name = stepData.Name,
            Description = stepData.Description,
            SqlValue = stepData.SqlValue
        };

        context.QuerySteps.Add(queryStep);
        await context.SaveChangesAsync(cancellationToken);

        // Create step parameters
        var parameters = ParameterEntityFactory.CreateQueryStepParameters(stepData.Parameters, queryStep.Id);
        context.QueryStepParameters.AddRange(parameters);

        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Message = "Query step added successfully",
            Success = true
        };
    }

    public async Task<BaseResponse> UpdateQueryStep(int queryId, int stepOrder, QueryStepData stepData, CancellationToken cancellationToken)
    {
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

    private async Task<QueryExecutionResult> ExecuteQuerySteps(Query query, string? finalQuery, List<ParameterValue>? parameters, CancellationToken cancellationToken)
    {
        var stepResults = new List<QueryStepResult>();
        using var virtualTableManager = new VirtualTableManager(loggerFactory.CreateLogger<VirtualTableManager>());
        var totalExecutionTime = 0.0;
        var dataSourceExecutionTimes = new Dictionary<string, double>();

        logger.LogInformation("Executing query chain {QueryId}: {StepCount} steps across {DataSourceCount} data sources",
            query.Id, query.Steps.Count, query.DataSourceIds.Count);

        // Execute each step against its own database
        foreach (var step in query.Steps.OrderBy(s => s.StepOrder))
        {
            logger.LogDebug("Executing step {StepOrder} against data source {DataSourceName} ({DatabaseEngine})",
                step.StepOrder, step.DataSource.Name, step.DataSource.DatabaseEngineType);

            var stepResult = await ExecuteStep(step, parameters);
            stepResults.Add(stepResult);
            totalExecutionTime += stepResult.ExecutionTimeMs;

            // Track execution time by data source
            var dataSourceKey = $"{step.DataSource.Name} ({step.DataSource.DatabaseEngineType})";
            dataSourceExecutionTimes[dataSourceKey] = dataSourceExecutionTimes.GetValueOrDefault(dataSourceKey, 0) + stepResult.ExecutionTimeMs;

            if (stepResult.Success)
            {
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
                logger.LogError("Step {StepOrder} failed: {ErrorMessage}", step.StepOrder, stepResult.ErrorMessage);
                break; // Stop execution on first failure
            }
        }

        QueryResult? finalResult = null;
        bool allStepsSucceeded = stepResults.All(s => s.Success);

        if (!string.IsNullOrEmpty(finalQuery) && allStepsSucceeded)
        {
            finalResult = await ExecuteFinalQuery(finalQuery, virtualTableManager);
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
            TotalExecutionTimeMs = totalExecutionTime,
            IsMultiStep = query.IsMultiStep,
            IsCrossDataSource = query.IsCrossDataSource,
            IsCrossDatabase = query.IsCrossDatabase,
            DataSourcesInvolved = stepResults.Select(s => s.DataSourceName).Distinct().ToList(),
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

    private async Task<QueryStepResult> ExecuteStep(QueryStep step, List<ParameterValue>? parameters)
    {
        if (!step.DataSource.DatabaseEngineType.HasValue)
            throw new BeaconException($"Data source {step.DataSourceId} is not a database type");

        var stepParameters = ExtractStepParameters(step, parameters);
        var (parameterizedSql, sqlParameters) = QueryHelper.PrepareParameterizedQuery(step.SqlValue, stepParameters);

        // Each step executes against its own data source/database
        var (results, executionTimeMs, timedOut) = await ExecuteQueryAsync(
            step.DataSource.DatabaseEngineType.Value,   // Each step can be different engine type!
            encryptionService.Decrypt(step.DataSource.EncryptedConnectionData),     // Each step connects to different database
            parameterizedSql,
            sqlParameters,
            null // Use default timeout
        );

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
            ExecutionTimeMs = executionTimeMs,
            Success = !timedOut,
            ErrorMessage = timedOut ? "Step execution timed out" : null
        };

        // Log manual query execution for step preview
        await queryExecutionLogger.LogQueryExecutionAsync(
            queryText: parameterizedSql,
            resultCount: results.Count,
            executionTimeMs: executionTimeMs,
            success: !timedOut,
            dataSourceId: step.DataSourceId,
            executionContext: "QueryStepPreview",
            errorMessage: timedOut ? "Step execution timed out" : null,
            userId: userContext.UserId,
            cancellationToken: CancellationToken.None);

        return stepResult;
    }

    private async Task<QueryResult> ExecuteFinalQuery(string finalQuery, VirtualTableManager virtualTableManager)
    {
        logger.LogInformation("Using in-memory SQLite database for final query execution");
        var inMemoryDbLogger = loggerFactory.CreateLogger<InMemoryDatabaseManager>();
        return await virtualTableManager.ExecuteFinalQueryWithInMemoryDatabase(
            finalQuery,
            inMemoryDbLogger,
            CancellationToken.None);
    }

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

    private async Task<DataSource> GetDataSource(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.DataSources.Where(ds => ds.Id == dataSourceId).SingleAsync(cancellationToken);
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

}
