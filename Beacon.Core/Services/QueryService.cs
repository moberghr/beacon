using System.Data.Common;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Adapters;
using Beacon.Core.Authorization;
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

    /// <summary>
    /// AI Actor ID if this query is managed by an AI Actor, null if user-created
    /// </summary>
    public int? AiActorId { get; set; }

    /// <summary>
    /// Name of the AI Actor managing this query
    /// </summary>
    public string? AiActorName { get; set; }

    /// <summary>
    /// Whether this query is locked from AI modifications
    /// </summary>
    public bool IsLocked { get; set; }

    public List<SubscriptionListData> Subscriptions { get; set; } = new();

    public List<NotificationStatisticsEntry> NotificationHistory { get; set; } = new();

    // Execution Time Statistics
    public double AvgExecutionTimeMs { get; set; }

    public double MinExecutionTimeMs { get; set; }

    public double MaxExecutionTimeMs { get; set; }

    public List<ExecutionTimeDataPoint> ExecutionTimeHistory { get; set; } = new();

    /// <summary>
    /// Cross-data-source computed properties
    /// </summary>
    public bool IsMultiStep => Steps.Count > 1;

    public bool IsCrossDataSource => Steps.Select(s => s.DataSourceId).Distinct().Count() > 1;

    public bool IsCrossDatabase => Steps.Select(s => s.DatabaseEngineType).Distinct().Count() > 1;

    public List<string> DataSourceNames => Steps.Select(s => s.DataSourceName).Distinct().ToList();

    public List<DatabaseEngineType> DatabaseEngines => Steps
        .Where(s => s.DatabaseEngineType.HasValue)
        .Select(s => s.DatabaseEngineType!.Value)
        .Distinct()
        .ToList();

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

    /// <summary>
    /// Filter by folder ID. Null means show all queries regardless of folder. Use -1 to show only root-level queries (no folder).
    /// </summary>
    public int? FolderId { get; set; }

    /// <summary>
    /// Search term to filter queries by name (case-insensitive partial match).
    /// </summary>
    public string? SearchTerm { get; set; }
}

internal partial class QueryService(IDbContextFactory<BeaconContext> contextFactory, IEncryptionService encryptionService, IManualQueryExecutionLogger queryExecutionLogger, ILogger<QueryService> logger, ILoggerFactory loggerFactory, IQueryVersionService queryVersionService, BeaconConfiguration beaconConfiguration, IBeaconUserContext userContext) : IQueryService
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
            var parameters = ParameterEntityFactory.CreateQueryStepParameters(stepData.Parameters, queryStep.Id);
            context.QueryStepParameters.AddRange(parameters);
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

        // Archive the query
        query.Archive();

        // Cascade-archive all subscriptions
        foreach (var subscription in query.Subscriptions)
        {
            subscription.Archive();
        }

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
            .WhereIf(request.FolderId.HasValue && request.FolderId.Value == -1, x => x.FolderId == null)
            .WhereIf(request.FolderId.HasValue && request.FolderId.Value != -1, x => x.FolderId == request.FolderId.Value)
            .WhereIf(!string.IsNullOrWhiteSpace(request.SearchTerm), x => x.Name.ToLower().Contains(request.SearchTerm!.ToLower()))
            .Select(x =>
                new QueryData
                {
                    QueryId = x.Id,
                    SubscriptionsCount = x.Subscriptions.Count,
                    CreatedTime = x.CreatedTime,
                    Name = x.Name,
                    Description = x.Description,
                    FolderId = x.FolderId,
                    FolderPath = x.Folder != null ? x.Folder.Path : null,
                    AiActorId = x.AiActorId,
                    AiActorName = x.AiActor != null ? x.AiActor.Name : null,
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
                    AiActorId = x.AiActorId,
                    AiActorName = x.AiActor != null ? x.AiActor.Name : null,
                    IsLocked = x.IsLocked,
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

        // Get execution time statistics for this query (all subscriptions, only successful executions)
        var executionTimeStats = await context.QueryExecutionHistory
            .Where(x => x.Subscription.QueryId == queryId && x.ExecutionTimeMs > 0)
            .GroupBy(x => 1)
            .Select(g => new
            {
                AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
                MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
                MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Get execution time history (last 30 days, grouped by date, only successful executions)
        var executionTimeHistory = await context.QueryExecutionHistory
            .Where(x => x.Subscription.QueryId == queryId && x.CreatedTime >= cutoffDate && x.ExecutionTimeMs > 0)
            .GroupBy(x => x.CreatedTime.Date)
            .Select(g => new ExecutionTimeDataPoint
            {
                Date = g.Key,
                AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
                MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
                MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        result.AvgExecutionTimeMs = executionTimeStats?.AvgExecutionTimeMs ?? 0;
        result.MinExecutionTimeMs = executionTimeStats?.MinExecutionTimeMs ?? 0;
        result.MaxExecutionTimeMs = executionTimeStats?.MaxExecutionTimeMs ?? 0;
        result.ExecutionTimeHistory = executionTimeHistory;

        return result;
    }
}
