using Microsoft.EntityFrameworkCore;
using Semantico.Core.Adapters;
using Semantico.Core.Adapters.Jira;
using Semantico.Core.Adapters.Mail;
using Semantico.Core.Adapters.Teams;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class NotificationService : INotificationService
{
    private readonly SemanticoContext _context;
    private readonly AdapterFactory _adapterFactory;

    public NotificationService(
        SemanticoContext context,
        AdapterFactory adapterFactory
    )
    {
        _context = context;
        _adapterFactory = adapterFactory;
    }

    public async Task SendNotificationAsync(NotificationType notificationType, RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount)
    {
        var adapter = _adapterFactory.GetAdapterService(notificationType);

        if (notificationType == NotificationType.Jira && lastExecutedQueryResultCount.HasValue)
        {
            await adapter.SendNotificationAsync(recipientQueryResult, lastExecutedQueryResultCount.Value);
        }
        else
        {
            await adapter.SendNotificationAsync(recipientQueryResult);
        }
    }

    public async Task<QueryExecutionHistoryListData> GetQueryExecutionHistoryAsync(int? subscriptionId, BaseListRequest request,
            int? lastQueryExecutionHistoryId, bool? notificationSent, CancellationToken cancellationToken)
    {
        var queryExecutionHistory = await _context.QueryExecutionHistory
            .WhereIf(subscriptionId.HasValue, x => x.SubscriptionId == subscriptionId)
            .WhereIf(lastQueryExecutionHistoryId.HasValue, x => x.Id < lastQueryExecutionHistoryId)
            .WhereIf(notificationSent.HasValue, x => x.NotificationSent == notificationSent)
            .OrderByDescending(x => x.Id)
            .Select(x =>
                new QueryExecutionHistoryData
                {
                    QueryExecutionHistoryId = x.Id,
                    Recipient = x.Recipient,
                    NotificationType = x.NotificationType,
                    ResultCount = x.ResultCount,
                    CreatedTime = x.CreatedTime,
                    NotificationSent = x.NotificationSent
                })
            .ToPagedListAsync(request, cancellationToken);

        return new QueryExecutionHistoryListData
        {
            LastQueryExecutionHistoryId = queryExecutionHistory.Items.LastOrDefault()?.QueryExecutionHistoryId,
            Data = queryExecutionHistory.Items,
            TotalCount = queryExecutionHistory.TotalCount
        };
    }
    
    public async Task<NotificationStatisticsData> GetNotificationStatisticsAsync(CancellationToken cancellationToken)
    {
        var cutoffDate = DateTime.UtcNow.AddDays(-30);

        var dates = await _context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= cutoffDate)
            .GroupBy(x => x.CreatedTime.Date)
            .Select(x => new NotificationDateStatisticsData()
            {
                Date = x.Key,
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationSent)
            })
            .ToListAsync(cancellationToken);

        return new NotificationStatisticsData
        {
            NotificationDateStatistics = dates
        };
    }
}

public interface INotificationService
{
    public Task SendNotificationAsync(NotificationType notificationType, RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount);

    Task<QueryExecutionHistoryListData> GetQueryExecutionHistoryAsync(int? subscriptionId, BaseListRequest request,
        int? lastQueryExecutionHistoryId, bool? notificationSent, CancellationToken cancellationToken);
    
    Task<NotificationStatisticsData> GetNotificationStatisticsAsync(CancellationToken cancellationToken);
}
