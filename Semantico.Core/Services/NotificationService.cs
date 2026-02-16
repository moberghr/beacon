using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class NotificationService(IDbContextFactory<SemanticoContext> contextFactory, AdapterFactory adapterFactory) : INotificationService
{
    public async Task SendNotification(RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount)
    {
        // Handle external notifications via adapters
        // Task creation is now handled directly in JobService.ExecuteQuery
        var adapter = adapterFactory.GetAdapterService(recipientQueryResult.RecipientNotificationType);
        await adapter.SendNotificationAsync(recipientQueryResult, lastExecutedQueryResultCount);
    }

    public async Task<QueryExecutionHistoryListData> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        
        var queryExecutionHistory = await context.QueryExecutionHistory
            .WhereIf(request.SubscriptionId.HasValue, x => x.SubscriptionId == request.SubscriptionId)
            .WhereIf(request.LastQueryExecutionHistoryId.HasValue, x => x.Id < request.LastQueryExecutionHistoryId)
            .WhereIf(request.NotificationStatus.HasValue, x => x.NotificationStatus == request.NotificationStatus)
            .Select(x => new QueryExecutionHistoryData {
                    QueryExecutionHistoryId = x.Id,
                    Notifications = x.Notifications.Select(y => new NotificationData
                    {
                        Id = y.Id,
                        Created = y.CreatedTime,
                        NotificationType = y.Type,
                        RecipientName = y.Recipient.Name,
                        SentAt = y.SentAt
                    }).ToList(),
                    ResultCount = x.ResultCount,
                    CreatedTime = x.CreatedTime,
                    NotificationStatus = x.NotificationStatus,
                    QueryName = x.Subscription.Query.Name,
                    SubscriptionId = x.SubscriptionId,
                    ExecutionTimeMs = x.ExecutionTimeMs,
                    Comment = x.Comment,
                    AiActorId = x.Subscription.AiActorId,
                    AiActorName = x.Subscription.AiActor != null ? x.Subscription.AiActor.Name : null
                })
            .ToPagedListAsync(request, cancellationToken);

        return new QueryExecutionHistoryListData
        {
            LastQueryExecutionHistoryId = queryExecutionHistory.Items.LastOrDefault()?.QueryExecutionHistoryId,
            Data = queryExecutionHistory.Items,
            TotalCount = queryExecutionHistory.TotalCount
        };
    }

    public async Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var cutoffDate = DateTime.UtcNow.AddDays(-30);

        // Get query execution statistics
        var queryStats = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= cutoffDate)
            .GroupBy(x => x.CreatedTime.Date)
            .Select(x => new
            {
                Date = x.Key,
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent)
            })
            .ToListAsync(cancellationToken);

        // Get migration execution statistics
        var migrationStats = await context.MigrationExecutions
            .Where(x => x.StartedAt >= cutoffDate)
            .GroupBy(x => x.StartedAt.Date)
            .Select(x => new
            {
                Date = x.Key,
                MigrationExecutions = x.Count(),
                SuccessfulMigrationExecutions = x.Count(m => m.Status == MigrationStatus.Completed)
            })
            .ToListAsync(cancellationToken);

        // Merge the data by date
        var allDates = queryStats.Select(x => x.Date)
            .Union(migrationStats.Select(x => x.Date))
            .Distinct()
            .OrderBy(x => x)
            .ToList();

        var dates = allDates.Select(date => new NotificationDateStatisticsData
        {
            Date = date,
            TotalQueries = queryStats.FirstOrDefault(x => x.Date == date)?.TotalQueries ?? 0,
            NotificationsSent = queryStats.FirstOrDefault(x => x.Date == date)?.NotificationsSent ?? 0,
            MigrationExecutions = migrationStats.FirstOrDefault(x => x.Date == date)?.MigrationExecutions ?? 0,
            SuccessfulMigrationExecutions = migrationStats.FirstOrDefault(x => x.Date == date)?.SuccessfulMigrationExecutions ?? 0
        }).ToList();

        return new NotificationStatisticsData
        {
            NotificationDateStatistics = dates
        };
    }

    public async Task<NotificationDetailsData?> GetNotificationDetails(int notificationId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.Notifications
            .Where(x => x.Id == notificationId)
            .Select(x => new NotificationDetailsData
            {
                Id = x.Id,
                CreatedTime = x.CreatedTime,
                SentAt = x.SentAt,
                Type = x.Type,
                Results = x.Results,
                RecipientName = x.Recipient.Name,
                QueryName = x.QueryExecutionHistory.Subscription.Query.Name,
                QueryId = x.QueryExecutionHistory.Subscription.QueryId,
                SubscriptionId = x.QueryExecutionHistory.SubscriptionId,
                ExecutionTimeMs = x.QueryExecutionHistory.ExecutionTimeMs,
                ResultCount = x.QueryExecutionHistory.ResultCount,
                NotificationStatus = x.QueryExecutionHistory.NotificationStatus
            })
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<QueryExecutionHistoryDetailsData?> GetQueryExecutionHistoryDetails(int queryExecutionHistoryId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var result = await context.QueryExecutionHistory
            .Where(x => x.Id == queryExecutionHistoryId)
            .Select(x => new
            {
                Id = x.Id,
                CreatedTime = x.CreatedTime,
                NotificationStatus = x.NotificationStatus,
                ExecutionTimeMs = x.ExecutionTimeMs,
                ResultCount = x.ResultCount,
                CompiledSql = x.CompiledSql,
                Results = x.Results,
                Comment = x.Comment,
                QueryName = x.Subscription.Query.Name,
                QueryId = x.Subscription.QueryId,
                SubscriptionId = x.SubscriptionId,
                CreateTasks = x.Subscription.CreateTasks,
                StoreResults = x.Subscription.StoreResults,
                NotificationList = x.Notifications.Select(n => new
                {
                    n.Id,
                    RecipientName = n.Recipient.Name,
                    n.Type,
                    n.SentAt,
                    n.Results,
                    n.TaskId
                }).ToList(),
                TaskIds = x.Notifications.Where(n => n.TaskId != null).Select(n => n.TaskId!.Value).Distinct().ToList()
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (result == null)
            return null;

        var tasks = new List<TaskSummaryData>();

        // If subscription has CreateTasks enabled, look up task by subscriptionId
        if (result.CreateTasks)
        {
            tasks = await context.QueryTasks
                .Where(t => t.SubscriptionId == result.SubscriptionId)
                .OrderByDescending(t => t.CreatedTime)
                .Take(1) // Get the most recent task for this subscription
                .Select(t => new TaskSummaryData
                {
                    Id = t.Id,
                    LatestResultCount = t.LatestResultCount,
                    CreatedAt = t.CreatedTime,
                    Resolved = t.Resolved,
                    ResolvedAt = t.ResolvedAt
                })
                .ToListAsync(cancellationToken);
        }
        else if (result.TaskIds.Any())
        {
            // Fallback: look up by notification TaskId links (legacy)
            tasks = await context.QueryTasks
                .Where(t => result.TaskIds.Contains(t.Id))
                .Select(t => new TaskSummaryData
                {
                    Id = t.Id,
                    LatestResultCount = t.LatestResultCount,
                    CreatedAt = t.CreatedTime,
                    Resolved = t.Resolved,
                    ResolvedAt = t.ResolvedAt
                })
                .ToListAsync(cancellationToken);
        }

        // Get results from QueryExecutionHistory first, fallback to notification results (legacy)
        var resultsJson = result.Results ?? result.NotificationList.FirstOrDefault()?.Results;

        return new QueryExecutionHistoryDetailsData
        {
            Id = result.Id,
            CreatedTime = result.CreatedTime,
            NotificationStatus = result.NotificationStatus,
            ExecutionTimeMs = result.ExecutionTimeMs,
            ResultCount = result.ResultCount,
            CompiledSql = result.CompiledSql,
            Results = resultsJson,
            Comment = result.Comment,
            QueryName = result.QueryName,
            QueryId = result.QueryId,
            SubscriptionId = result.SubscriptionId,
            Notifications = result.NotificationList.Select(n => new NotificationSummaryData
            {
                Id = n.Id,
                RecipientName = n.RecipientName,
                Type = n.Type,
                SentAt = n.SentAt
            }).ToList(),
            Tasks = tasks
        };
    }
}