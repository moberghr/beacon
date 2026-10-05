using Beacon.Core.Helpers;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.QueryExecutionHistory;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Notifications;

internal sealed class GetNotificationsHandler(INotificationService notificationService)
    : IRequestHandler<GetNotificationsQuery, PagedList<NotificationEntry>>
{
    public async Task<PagedList<NotificationEntry>> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        var serviceRequest = new GetQueryExecutionHistoryRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            NotificationStatus = request.Status,
            SubscriptionId = request.SubscriptionId,
        };

        var data = await notificationService.GetQueryExecutionHistory(serviceRequest, cancellationToken);

        return data.Map(x =>
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
                x.Notifications
                    .Select(y => y.RecipientName)
                    .ToList()));
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise; sortable by any scalar column of the row.</summary>
public record GetNotificationsQuery : ListRequest, IRequest<PagedList<NotificationEntry>>
{
    public NotificationStatus? Status { get; init; }

    public int? SubscriptionId { get; init; }
}

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
