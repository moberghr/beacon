using Beacon.Core.Data.Enums;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Notifications;

internal sealed class GetNotificationsHandler(INotificationService notificationService)
    : IRequestHandler<GetNotificationsQuery, GetNotificationsResult>
{
    public async Task<GetNotificationsResult> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        var serviceRequest = new GetQueryExecutionHistoryRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            NotificationStatus = request.NotificationStatus,
            SubscriptionId = request.SubscriptionId,
        };

        var data = await notificationService.GetQueryExecutionHistory(serviceRequest, cancellationToken);

        var entries = data.Data
            .Select(x =>
                new NotificationEntry(
                    x.QueryExecutionHistoryId,
                    x.SubscriptionId,
                    x.QueryName,
                    x.NotificationStatus,
                    x.ResultCount,
                    x.ExecutionTimeMs,
                    x.CreatedTime,
                    x.AiActorId,
                    x.AiActorName,
                    x.Comment,
                    x.Notifications.Select(y => y.RecipientName).ToList()))
            .ToList();

        return new GetNotificationsResult(entries, data.TotalCount ?? entries.Count);
    }
}

public record GetNotificationsQuery(
    int Page = 0,
    int PageSize = 100,
    NotificationStatus? NotificationStatus = null,
    int? SubscriptionId = null) : IRequest<GetNotificationsResult>;

public record GetNotificationsResult(List<NotificationEntry> Entries, int TotalCount);

public record NotificationEntry(
    int Id,
    int SubscriptionId,
    string QueryName,
    NotificationStatus Status,
    int ResultCount,
    double ExecutionTimeMs,
    DateTime CreatedTime,
    int? AiActorId,
    string? AiActorName,
    string? Comment,
    List<string> RecipientNames);
