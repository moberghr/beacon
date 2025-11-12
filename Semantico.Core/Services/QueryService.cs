using System.Data.Common;
using System.Data.SqlClient;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MySql.Data.MySqlClient;
using Npgsql;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Helpers.File;
using Semantico.Core.Models;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Recipients;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Validators;

namespace Semantico.Core.Services;

public interface IQueryService
{
    // ===== EXISTING METHODS (unchanged signatures for backward compatibility) =====
    Task<BaseResponse> CreateQuery(QueryData queryData, CancellationToken cancellationToken);

    Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken);

    Task DeleteQuery(int queryId, CancellationToken cancellationToken);

    Task<PagedList<QueryData>> GetQueries(GetQueriesRequest request, CancellationToken cancellationToken);

    Task<QueryDetailsData> GetQueryDetails(int queryId, CancellationToken cancellationToken);
    
    Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken);
    
    // ===== ENHANCED METHODS FOR CROSS-DATA-SOURCE FUNCTIONALITY =====
    Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, CancellationToken cancellationToken = default);
    
    Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken);
    
    Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken);
    
    // Step management with data source context
    Task<BaseResponse> AddQueryStep(int queryId, QueryStepData stepData, CancellationToken cancellationToken);
    
    Task<BaseResponse> UpdateQueryStep(int queryId, int stepOrder, QueryStepData stepData, CancellationToken cancellationToken);
    
    Task DeleteQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken);
}

public class QueryDetailsData
{
    public int Id { get; set; }

    public string Name { get; set; } = null!;

    public string? Description { get; set; }

    public DateTime CreatedTime { get; set; }

    public int TotalExecutions { get; set; }

    public int SentNotifications { get; set; }

    public List<QueryStepData> Steps { get; set; } = new();
    
    /// <summary>
    /// Final query to execute against the in-memory SQLite database with all step results loaded
    /// Uses @result1, @result2, etc. to reference previous step results
    /// </summary>
    public string? FinalQuery { get; set; }

    /// <summary>
    /// Data Source ID where the final query should be executed (for database engine context)
    /// If null, defaults to the first step's data source
    /// </summary>
    public int? FinalQueryDataSourceId { get; set; }
    
    public List<SubscriptionListData> Subscriptions { get; set; } = new();
    
    public List<NotificationStatisticsEntry> NotificationHistory { get; set; } = new();

    /// <summary>
    /// Cross-data-source computed properties
    /// </summary>
    public bool IsMultiStep => Steps.Count > 1;

    public bool IsCrossDataSource => Steps.Select(s => s.DataSourceId).Distinct().Count() > 1;

    public bool IsCrossDatabase => Steps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;

    public List<string> DataSourceNames => Steps.Select(s => s.DataSourceName).Distinct().ToList();
    
    public List<DatabaseEngineType> DatabaseEngines => Steps.Select(s => s.DatabaseEngineType).Distinct().ToList();

    /// <summary>
    /// Backward compatibility properties (map to first step)
    /// </summary>
    public string SqlValue => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.SqlValue ?? "";

    public string DataSourceName => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.DataSourceName ?? "";
    
    public List<QueryParameterData> Parameters => Steps.OrderBy(s => s.StepOrder).FirstOrDefault()?.Parameters.Select(p => new QueryParameterData
    {
        Name = p.Name,
        Type = p.Type,
        Description = p.Description ?? "",
        Placeholder = p.Placeholder ?? ""
    }).ToList() ?? new();
}

public class NotificationStatisticsEntry
{
    public DateTime Date { get; set; }
    public int TotalExecutions { get; set; }
    public int SuccessfulNotifications { get; set; }
    public int FailedExecutions { get; set; }
    public double SuccessRate => TotalExecutions > 0 ? (double)SuccessfulNotifications / TotalExecutions * 100 : 0;
}

public class SubscriptionListData
{
    public int SubscriptionId { get; set; }

    public DateTime CreatedTime { get; set; }
    public string Name { get; set; }
    
    public string Subscribers { get; set; }

    public string CronExpression { get; set; }
}

public class GetQueriesRequest : SortedListRequest
{
    public int? QueryId { get; set; }
    public int? DataSourceId { get; set; }

    public string? QueryName { get; set; }
}

internal class QueryService(IDbContextFactory<SemanticoContext> contextFactory, IEncryptionService encryptionService, ILogger<QueryService> logger, ILoggerFactory loggerFactory) : IQueryService
{
    public async Task<BaseResponse> CreateQuery(QueryData queryData, CancellationToken cancellationToken)
    {
        // Ensure backward compatibility - if no steps, create one from the legacy properties
        if (!queryData.Steps.Any())
        {
            QueryValidator.CheckForFlaggedWords(queryData.SqlValue);
            QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);
            
            queryData.Steps.Add(new QueryStepData
            {
                StepOrder = 1,
                Name = "Step 1",
                SqlValue = queryData.SqlValue,
                DataSourceId = queryData.DataSourceId,
                DataSourceName = queryData.DataSourceName ?? "",
                DatabaseEngineType = DatabaseEngineType.PostgreSQL, // Will be set correctly from database
                Parameters = queryData.Parameters.Select(p => new QueryStepParameterData
                {
                    Name = p.Name,
                    Type = p.Type,
                    Description = p.Description,
                    Placeholder = p.Placeholder
                }).ToList()
            });
        }

        // Validate all steps
        foreach (var step in queryData.Steps)
        {
            QueryValidator.CheckForFlaggedWords(step.SqlValue);
            QueryValidator.CheckForParameters(step.SqlValue, step.Parameters.Select(p => new QueryParameterData
            {
                Name = p.Name,
                Type = p.Type,
                Description = p.Description ?? "",
                Placeholder = p.Placeholder ?? ""
            }).ToList());
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var query = new Query
        {
            Name = queryData.Name,
            Description = queryData.Description,
            FinalQuery = queryData.FinalQuery
        };

        context.Queries.Add(query);
        await context.SaveChangesAsync(cancellationToken); // Save to get query ID

        // Create query steps
        foreach (var stepData in queryData.Steps)
        {
            var queryStep = new QueryStep
            {
                QueryId = query.Id,
                DataSourceId = stepData.DataSourceId,
                StepOrder = stepData.StepOrder,
                Name = stepData.Name,
                Description = stepData.Description,
                SqlValue = stepData.SqlValue
            };

            context.QuerySteps.Add(queryStep);
            await context.SaveChangesAsync(cancellationToken); // Save to get step ID

            // Create step parameters
            foreach (var parameterData in stepData.Parameters)
            {
                var parameter = new QueryStepParameter
                {
                    QueryStepId = queryStep.Id,
                    Name = parameterData.Name,
                    Type = parameterData.Type,
                    Description = parameterData.Description,
                    Placeholder = parameterData.Placeholder
                };

                context.QueryStepParameters.Add(parameter);
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Message = "Saved successfully",
            Success = true
        };
    }

    public async Task DeleteQuery(int queryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var query = await context.Queries
            .Include(x => x.Steps)
                .ThenInclude(s => s.Parameters)
            .Include(x => x.Subscriptions)
            .Where(x => x.Id == queryId)
            .SingleAsync(cancellationToken);

        if (query.Subscriptions.Count > 0)
        {
            throw new SemanticoException($"Unable to remove query due to active subscriptions.");
        }

        query.Archive();

        // Archive all steps and their parameters
        foreach (var step in query.Steps)
        {
            foreach (var param in step.Parameters)
            {
                context.QueryStepParameters.Remove(param);
            }
            context.QuerySteps.Remove(step);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedList<QueryData>> GetQueries(GetQueriesRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var results = await context.Queries
            .WhereIf(request.QueryId.HasValue, x => x.Id == request.QueryId)
            .WhereIf(request.QueryName != null, x => x.Name == request.QueryName)
            .WhereIf(request.DataSourceId.HasValue, x => x.Steps.Any(s => s.DataSourceId == request.DataSourceId))
            .Select(x =>
                new QueryData
                {
                    QueryId = x.Id,
                    SubscriptionsCount = x.Subscriptions.Count,
                    CreatedTime = x.CreatedTime,
                    Name = x.Name,
                    Description = x.Description,
                    Steps = x.Steps.OrderBy(s => s.StepOrder).Select(s => new QueryStepData
                    {
                        StepId = s.Id,
                        StepOrder = s.StepOrder,
                        Name = s.Name ?? $"Step {s.StepOrder}",
                        Description = s.Description,
                        SqlValue = s.SqlValue,
                        DataSourceId = s.DataSourceId,
                        DataSourceName = s.DataSource.Name,
                        DatabaseEngineType = s.DataSource.DatabaseEngineType,
                        Parameters = s.Parameters.Select(p => new QueryStepParameterData
                        {
                            Name = p.Name,
                            Type = p.Type,
                            Description = p.Description,
                            Placeholder = p.Placeholder
                        }).ToList()
                    }).ToList()
                })
            .ToPagedListAsync(request, cancellationToken);

        return results;
    }

    public async Task<QueryDetailsData> GetQueryDetails(int queryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var result = await context.Queries
            .Include(x => x.Steps)
                .ThenInclude(s => s.DataSource)
            .Include(x => x.Steps)
                .ThenInclude(s => s.Parameters)
            .Include(x => x.Subscriptions)
                .ThenInclude(s => s.Recipients)
            .Include(x => x.Subscriptions)
                .ThenInclude(s => s.QueryExecutionHistory)
            .AsSplitQuery()
            .Where(x => x.Id == queryId)
            .Select(x =>
                new QueryDetailsData
                {
                    Id = x.Id,
                    CreatedTime = x.CreatedTime,
                    Name = x.Name,
                    Description = x.Description,
                    FinalQuery = x.FinalQuery,
                    TotalExecutions = x.Subscriptions.Sum(y => y.QueryExecutionHistory.Count),
                    SentNotifications = x.Subscriptions.Sum(y => y.QueryExecutionHistory.Count(z => z.NotificationStatus == NotificationStatus.NotificationSent)),
                    Steps = x.Steps.OrderBy(s => s.StepOrder).Select(s => new QueryStepData
                    {
                        StepId = s.Id,
                        StepOrder = s.StepOrder,
                        Name = s.Name ?? $"Step {s.StepOrder}",
                        Description = s.Description,
                        SqlValue = s.SqlValue,
                        DataSourceId = s.DataSourceId,
                        DataSourceName = s.DataSource.Name,
                        DatabaseEngineType = s.DataSource.DatabaseEngineType,
                        Parameters = s.Parameters.Select(p => new QueryStepParameterData
                        {
                            Name = p.Name,
                            Type = p.Type,
                            Description = p.Description,
                            Placeholder = p.Placeholder
                        }).ToList()
                    }).ToList(),
                    Subscriptions = x.Subscriptions.Select(y =>
                        new SubscriptionListData
                        {
                            SubscriptionId = y.Id,
                            Name = y.Query.Name,
                            CronExpression = y.CronExpression,
                            CreatedTime = y.CreatedTime,
                            Subscribers = y.Recipients.Select(r => r.Name).ToJson()
                        }).ToList()
                }).SingleAsync(cancellationToken);
        
        // Get notification history for the last 30 days
        var cutoffDate = DateTime.UtcNow.AddDays(-30);
        var notificationHistory = await context.QueryExecutionHistory
            .Where(x => x.Subscription.QueryId == queryId && x.CreatedTime >= cutoffDate)
            .GroupBy(x => x.CreatedTime.Date)
            .Select(x => new NotificationStatisticsEntry
            {
                Date = x.Key,
                TotalExecutions = x.Count(),
                SuccessfulNotifications = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent),
                FailedExecutions = x.Count(y => y.NotificationStatus != NotificationStatus.NotificationSent)
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);
        
        result.NotificationHistory = notificationHistory;
        
        return result;
    }

    public async Task<QueryResult> ExecuteQuery(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var subscription = await context.Subscriptions
            .Include(x => x.Recipients)
            .Include(x => x.Query)
                .ThenInclude(q => q.Steps)
                    .ThenInclude(s => s.DataSource)
            .Include(x => x.Query)
                .ThenInclude(q => q.Steps)
                    .ThenInclude(s => s.Parameters)
            .Where(x => x.Id == subscriptionId)
            .SingleAsync(cancellationToken);

        // Use the advanced step-based execution with stored final query if available
        var executionResult = await ExecuteQueryAdvanced(
            subscription.QueryId, 
            finalQuery: subscription.Query.FinalQuery, 
            parameters: null, 
            cancellationToken);

        // Convert QueryExecutionResult to QueryResult for backward compatibility
        QueryResult queryResult;

        if (executionResult.FinalResult != null)
        {
            // Multi-step query or query with final result
            queryResult = executionResult.FinalResult;
        }
        else if (executionResult.StepResults.Any() && executionResult.StepResults.Last().Success)
        {
            // Single-step query - convert the step result to QueryResult
            var lastStep = executionResult.StepResults.Last();
            queryResult = new QueryResult
            {
                QueryResults = JsonSerializer.Serialize(lastStep.PreviewResults),
                TotalRecords = lastStep.TotalRows,
                DataSourceName = lastStep.DataSourceName,
                SqlQuery = lastStep.SqlQuery,
                SubscriptionName = subscription.Query.Name,
                SubscriptionId = subscriptionId,
                TopRecords = lastStep.PreviewResults,
                AllRecords = lastStep.AllResults,
                ExecutionTimeMs = lastStep.ExecutionTimeMs,
                TimedOut = !lastStep.Success,
                Recipients = new List<RecipientData>()
            };
        }
        else
        {
            // Execution failed
            throw new SemanticoException($"Query execution failed: {executionResult.ErrorMessage ?? "Unknown error"}");
        }

        // Apply subscription-specific settings and recipients
        queryResult.Recipients = subscription.Recipients.Select(r => new RecipientData
        {
            RecipientId = r.Id,
            Name = r.Name,
            Description = r.Description,
            Destination = r.Destination,
            NotificationType = r.NotificationType
        }).ToList();

        queryResult.MaxRows = subscription.MaxRows ?? 1_000_000;
        
        queryResult.TopRecords = queryResult.TopRecords.Take(queryResult.MaxRows.Value).ToList();
        queryResult.AllRecords = queryResult.AllRecords.Take(queryResult.MaxRows.Value).ToList();
        
        // Need to create a new QueryResult with updated QueryResults since it's init-only
        queryResult = new QueryResult
        {
            QueryResults = JsonSerializer.Serialize(queryResult.TopRecords),
            TotalRecords = queryResult.TotalRecords,
            DataSourceName = queryResult.DataSourceName,
            SqlQuery = queryResult.SqlQuery,
            SubscriptionName = subscription.Query.Name,
            SubscriptionId = subscriptionId,
            ShowQuery = queryResult.ShowQuery,
            MaxRows = queryResult.MaxRows,
            Recipients = queryResult.Recipients,
            TopRecords = queryResult.TopRecords,
            AllRecords = queryResult.AllRecords,
            ExecutionTimeMs = queryResult.ExecutionTimeMs,
            TimedOut = queryResult.TimedOut,
            SaveResults = subscription.StoreResults
        };

        
        return queryResult;
    }

    public async Task<BaseResponse> UpdateQuery(QueryData queryData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var query = await context.Queries
            .Include(x => x.Steps)
                .ThenInclude(s => s.Parameters)
            .Where(x => x.Id == queryData.QueryId)
            .SingleAsync(cancellationToken);

        // Update basic query properties
        query.Name = queryData.Name;
        query.Description = queryData.Description;
        query.FinalQuery = queryData.FinalQuery;

        // Handle backward compatibility - if no steps provided, update from legacy properties
        if (!queryData.Steps.Any() && !string.IsNullOrEmpty(queryData.SqlValue))
        {
            QueryValidator.CheckForFlaggedWords(queryData.SqlValue);
            QueryValidator.CheckForParameters(queryData.SqlValue, queryData.Parameters);
            
            // Update the first (and typically only) step for backward compatibility
            var firstStep = query.Steps.OrderBy(s => s.StepOrder).First();
            firstStep.SqlValue = queryData.SqlValue;
            firstStep.DataSourceId = queryData.DataSourceId;

            // Remove existing parameters
            foreach (var param in firstStep.Parameters.ToList())
            {
                context.QueryStepParameters.Remove(param);
            }

            // Add new parameters
            foreach (var parameterData in queryData.Parameters)
            {
                var parameter = new QueryStepParameter
                {
                    QueryStepId = firstStep.Id,
                    Name = parameterData.Name,
                    Type = parameterData.Type,
                    Description = parameterData.Description,
                    Placeholder = parameterData.Placeholder
                };

                context.QueryStepParameters.Add(parameter);
            }
        }
        else
        {
            // Handle multi-step updates
            foreach (var stepData in queryData.Steps)
            {
                QueryValidator.CheckForFlaggedWords(stepData.SqlValue);
                
                var step = query.Steps.FirstOrDefault(s => s.Id == stepData.StepId);
                if (step != null)
                {
                    step.DataSourceId = stepData.DataSourceId;
                    step.Name = stepData.Name;
                    step.Description = stepData.Description;
                    step.SqlValue = stepData.SqlValue;

                    // Remove existing parameters
                    foreach (var param in step.Parameters.ToList())
                    {
                        context.QueryStepParameters.Remove(param);
                    }

                    // Add new parameters
                    foreach (var parameterData in stepData.Parameters)
                    {
                        var parameter = new QueryStepParameter
                        {
                            QueryStepId = step.Id,
                            Name = parameterData.Name,
                            Type = parameterData.Type,
                            Description = parameterData.Description,
                            Placeholder = parameterData.Placeholder
                        };

                        context.QueryStepParameters.Add(parameter);
                    }
                }
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        return new BaseResponse
        {
            Message = "Saved successfully",
            Success = true
        };
    }
    
    private async Task<(List<IDictionary<string, object?>> Results, double ExecutionTimeMs, bool TimedOut)> ExecuteQueryAsync(
        DatabaseEngineType dbEngineType, 
        string connectionString, 
        string sqlQuery, 
        int? timeoutSeconds = null)
    {
        await using var connection = DbConnectionFactory.CreateConnection(dbEngineType, connectionString);
        await connection.OpenAsync();

        // Replace newline, carriage return, and tab characters with a space
        var cleanedSql = sqlQuery.Replace("\n", " ")
            .Replace("\r", " ")
            .Replace("\t", " ")
            .Trim();

        var stopwatch = new System.Diagnostics.Stopwatch();
        stopwatch.Start();
        
        try
        {
            // Create a cancellation token source with timeout if specified
            using var timeoutCts = timeoutSeconds.HasValue 
                ? new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds.Value)) 
                : new CancellationTokenSource();
            
            // Combine with the provided cancellation token if needed
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
            
            // Execute the query with the timeout
            var commandDefinition = new CommandDefinition(
                commandText: cleanedSql,
                commandTimeout: timeoutSeconds,
                cancellationToken: linkedCts.Token
            );
            
            var dapperRows = await connection.QueryAsync(commandDefinition);
            
            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;

            // Convert the dapper rows to a list of dictionaries for reflection and serialization
            var results = dapperRows.Select(x => (IDictionary<string, object?>)x).ToList();
            return (results, executionTimeMs, false);
        }
        catch (TaskCanceledException)
        {
            // Query was cancelled due to timeout
            stopwatch.Stop();
            var executionTimeMs = stopwatch.Elapsed.TotalMilliseconds;
            
            // Return empty results with timeout indicator
            return (new List<IDictionary<string, object?>>(), executionTimeMs, true);
        }
        catch (Exception)
        {
            stopwatch.Stop();
            // Re-throw other exceptions
            throw;
        }
    }

    // ===== ENHANCED METHODS FOR CROSS-PROJECT FUNCTIONALITY =====
    
    public async Task<QueryExecutionResult> ExecuteQueryAdvanced(int queryId, string? finalQuery = null, List<ParameterValue>? parameters = null, CancellationToken cancellationToken = default)
    {
        var query = await GetQueryWithSteps(queryId, cancellationToken);
        
        // Use provided parameters or fall back to stored values from query
        var effectiveFinalQuery = finalQuery ?? query.FinalQuery;

        // All queries use the same execution path - handles single-DB, multi-step, and cross-DB!
        return await ExecuteQuerySteps(query, effectiveFinalQuery, parameters, cancellationToken);
    }

    public async Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, CancellationToken cancellationToken)
    {
        return await PreviewQueryStep(queryId, stepOrder, null, cancellationToken);
    }
    
    public async Task<QueryStepResult> PreviewQueryStep(int queryId, int stepOrder, List<ParameterValue>? parameters, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var step = await context.QuerySteps
            .Include(s => s.DataSource)
            .Include(s => s.Parameters)
            .Where(s => s.QueryId == queryId && s.StepOrder == stepOrder)
            .SingleAsync(cancellationToken);

        return await ExecuteStep(step, parameters);
    }

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
        foreach (var parameterData in stepData.Parameters)
        {
            var parameter = new QueryStepParameter
            {
                QueryStepId = queryStep.Id,
                Name = parameterData.Name,
                Type = parameterData.Type,
                Description = parameterData.Description,
                Placeholder = parameterData.Placeholder
            };

            context.QueryStepParameters.Add(parameter);
        }

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
        foreach (var parameterData in stepData.Parameters)
        {
            var parameter = new QueryStepParameter
            {
                QueryStepId = queryStep.Id,
                Name = parameterData.Name,
                Type = parameterData.Type,
                Description = parameterData.Description,
                Placeholder = parameterData.Placeholder
            };

            context.QueryStepParameters.Add(parameter);
        }

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
            throw new SemanticoException("Cannot delete the last remaining query step.");
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
                    DatabaseEngine = step.DataSource.DatabaseEngineType.ToString(),
                    DatabaseEngineType = step.DataSource.DatabaseEngineType
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
        
        return new QueryExecutionResult
        {
            StepResults = stepResults,
            FinalResult = finalResult,
            Success = allStepsSucceeded,
            TotalExecutionTimeMs = totalExecutionTime,
            IsMultiStep = query.IsMultiStep,
            IsCrossDataSource = query.IsCrossDataSource,
            IsCrossDatabase = query.IsCrossDatabase,
            DataSourcesInvolved = stepResults.Select(s => s.DataSourceName).Distinct().ToList(),
            DatabaseEnginesUsed = stepResults.Select(s => s.DatabaseEngineType).Distinct().ToList(),
            ExecutionTimeByDataSource = dataSourceExecutionTimes
        };
    }

    private async Task<QueryStepResult> ExecuteStep(QueryStep step, List<ParameterValue>? parameters)
    {
        var stepParameters = ExtractStepParameters(step, parameters);
        var compiledSql = QueryHelper.CompileSql(step.SqlValue, stepParameters);

        // Each step executes against its own data source/database
        var (results, executionTimeMs, timedOut) = await ExecuteQueryAsync(
            step.DataSource.DatabaseEngineType,   // Each step can be different engine type!
            encryptionService.Decrypt(step.DataSource.ConnectionString),     // Each step connects to different database
            compiledSql,
            null // Use default timeout
        );
        
        return new QueryStepResult
        {
            StepOrder = step.StepOrder,
            StepName = step.Name ?? $"Step {step.StepOrder}",
            SqlQuery = compiledSql,
            DataSourceName = step.DataSource.Name,
            DatabaseEngine = step.DataSource.DatabaseEngineType.ToString(),
            DatabaseEngineType = step.DataSource.DatabaseEngineType,
            PreviewResults = results.Take(10).ToList(),
            AllResults = results,
            TotalRows = results.Count,
            ExecutionTimeMs = executionTimeMs,
            Success = !timedOut,
            ErrorMessage = timedOut ? "Step execution timed out" : null
        };
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

    private List<SubscriptionParamaterData> ExtractStepParameters(QueryStep step, List<ParameterValue>? parameters)
    {
        if (parameters == null || !parameters.Any())
            return new List<SubscriptionParamaterData>();

        return step.Parameters.Select(p =>
        {
            var value = parameters.FirstOrDefault(param => param.Name == p.Name)?.Value ?? "";
            return new SubscriptionParamaterData
            {
                QueryPlaceholder = p.Name,
                Value = value
            };
        }).ToList();
    }

}