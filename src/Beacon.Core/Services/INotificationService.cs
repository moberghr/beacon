using Beacon.Core.Adapters;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Notifications;

namespace Beacon.Core.Services;

public interface INotificationService
{
    Task SendNotification(
        RecipientQueryResult recipientQueryResult,
        int? lastExecutedQueryResultCount,
        CancellationToken cancellationToken = default);

    Task<PagedList<QueryExecutionHistoryData>> GetQueryExecutionHistory(GetQueryExecutionHistoryRequest request, CancellationToken cancellationToken);

    Task<NotificationStatisticsData> GetNotificationStatistics(CancellationToken cancellationToken);

    /// <summary>
    /// One run with its stored result rows, or null when it does not exist or is not readable within
    /// <paramref name="scope"/>.
    /// </summary>
    Task<NotificationDetailsData?> GetNotificationDetails(
        int queryExecutionHistoryId,
        StoredRunScope scope,
        CancellationToken cancellationToken);
}

public record GetQueryExecutionHistoryRequest : ListRequest
{
    public int? SubscriptionId { get; init; }

    public NotificationStatus? NotificationStatus { get; init; }

    /// <summary>Only the runs readable within this scope are listed.</summary>
    public required StoredRunScope Scope { get; init; }
}
