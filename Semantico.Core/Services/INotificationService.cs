using Semantico.Core.Adapters;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

public interface INotificationService
{
    Task SendNotification(RecipientQueryResult recipientQueryResult, int? lastExecutedQueryResultCount);

    Task<QueryExecutionHistoryListData> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken);

    Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken);

    Task<NotificationDetailsData?> GetNotificationDetails(int notificationId, CancellationToken cancellationToken);

    Task<QueryExecutionHistoryDetailsData?> GetQueryExecutionHistoryDetails(int queryExecutionHistoryId, CancellationToken cancellationToken);
}

public class GetQueryExecutionHistoryRequest : SortedListRequest
{
    public int? SubscriptionId { get; set; }
    public int? LastQueryExecutionHistoryId { get; set; }
    public NotificationStatus? NotificationStatus { get; set; }
}
