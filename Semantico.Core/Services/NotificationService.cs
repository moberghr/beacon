using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class NotificationService : INotificationService
{
    private readonly SemanticoContext _context;
    private readonly AdapterFactory _adapterFactory;

    public NotificationService(SemanticoContext context, AdapterFactory adapterFactory)
    {
        _context = context;
        _adapterFactory = adapterFactory;
    }

    public async Task SendNotification(RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount)
    {
        var adapter = _adapterFactory.GetAdapterService(recipientQueryResult.RecipientNotificationType);
        
        await adapter.SendNotificationAsync(recipientQueryResult, lastExecutedQueryResultCount);
    }

    public async Task<QueryExecutionHistoryListData> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        var queryExecutionHistory = await _context.QueryExecutionHistory
            .WhereIf(request.SubscriptionId.HasValue, x => x.SubscriptionId == request.SubscriptionId)
            .WhereIf(request.LastQueryExecutionHistoryId.HasValue, x => x.Id < request.LastQueryExecutionHistoryId)
            .WhereIf(request.NotificationStatus.HasValue, x => x.NotificationStatus == request.NotificationStatus)
            .SelectMany(x => x.Subscription.Recipients
                .Select(y => new QueryExecutionHistoryData {
                    QueryExecutionHistoryId = x.Id,
                    RecipientName = y.Name,
                    NotificationType = y.NotificationType,
                    ResultCount = x.ResultCount,
                    CreatedTime = x.CreatedTime,
                    NotificationStatus = x.NotificationStatus,
                    QueryName = x.Subscription.Query.Name,
                    SubscriptionId = x.SubscriptionId,
                    ExecutionTimeMs = x.ExecutionTimeMs
                })
                .ToList())
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
        var cutoffDate = DateTime.UtcNow.AddDays(-60);

        var dates = await _context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= cutoffDate)
            .GroupBy(x => x.CreatedTime.Date)
            .Select(x => new NotificationDateStatisticsData()
            {
                Date = x.Key,
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent)
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        return new NotificationStatisticsData
        {
            NotificationDateStatistics = dates
        };
    }
}

public interface INotificationService
{
    Task SendNotification(RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount);

    Task<QueryExecutionHistoryListData> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken);

    Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken);
}

public class GetQueryExecutionHistoryRequest : SortedListRequest
{
    public int? SubscriptionId { get; set; }
    public int? LastQueryExecutionHistoryId { get; set; }
    public NotificationStatus? NotificationStatus { get; set; }
}