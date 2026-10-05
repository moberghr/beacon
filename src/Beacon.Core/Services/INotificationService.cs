using Beacon.Core.Adapters;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.QueryExecutionHistory;

namespace Beacon.Core.Services;

public interface INotificationService
{
    Task SendNotification(
        RecipientQueryResult recipientQueryResult,
        int? lastExecutedQueryResultCount,
        CancellationToken cancellationToken = default);

    Task<PagedList<QueryExecutionHistoryData>> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken);

    Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken);

    Task<NotificationDetailsData?> GetNotificationDetails(int queryExecutionHistoryId, CancellationToken cancellationToken);

    Task<QueryExecutionHistoryDetailsData?> GetQueryExecutionHistoryDetails(int queryExecutionHistoryId, CancellationToken cancellationToken);
}

public record GetQueryExecutionHistoryRequest : ListRequest
{
    public int? SubscriptionId { get; init; }

    public NotificationStatus? NotificationStatus { get; init; }
}
