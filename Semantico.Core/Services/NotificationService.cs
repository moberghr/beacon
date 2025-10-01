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
                    ExecutionTimeMs = x.ExecutionTimeMs
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
}

public interface INotificationService
{
    Task SendNotification(RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount);

    Task<QueryExecutionHistoryListData> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken);

    Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken);

    Task<NotificationDetailsData?> GetNotificationDetails(int notificationId, CancellationToken cancellationToken);
}

public class GetQueryExecutionHistoryRequest : SortedListRequest
{
    public int? SubscriptionId { get; set; }
    public int? LastQueryExecutionHistoryId { get; set; }
    public NotificationStatus? NotificationStatus { get; set; }
}