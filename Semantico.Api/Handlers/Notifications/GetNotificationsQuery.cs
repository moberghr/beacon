using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Enums;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Notifications;

public class GetNotificationsQuery : IRequestHandler<GetNotificationsRequest, GetNotificationsResponse>
{
    private readonly SemanticoContext _context;

    public GetNotificationsQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetNotificationsResponse> Handle(GetNotificationsRequest request, CancellationToken cancellationToken)
    {
        var notifications = await _context.Notifications
            .Where(x => x.SubscriptionId == request.SubscriptionId)
            .WhereIf(request.LastNotificationId.HasValue, x => x.Id > request.LastNotificationId)
            .OrderByDescending(x => x.Id)
            .TakeIf(request.PageSize.HasValue, request.PageSize)
            .Select(x =>
                new GetNotificationsResponseDataList
                {
                    NotificationId = x.Id,
                    Recipient = x.Recipient,
                    NotificationType = x.NotificationType,
                    ResultCount = x.ResultCount
                })
            .ToListAsync(cancellationToken);

        return new GetNotificationsResponse
        {
            LastNotificationId = notifications.Last().NotificationId,
            Notifications = notifications
        };
    }
}

public class GetNotificationsRequest : IRequest<GetNotificationsResponse>
{
    public required int SubscriptionId { get; init; }

    public int? PageSize { get; init; }

    public int? LastNotificationId { get; init; }
}

public class GetNotificationsResponse
{
    public required List<GetNotificationsResponseDataList> Notifications { get; set; }

    public int? LastNotificationId { get; init; }

}

public class GetNotificationsResponseDataList
{
    public required int NotificationId { get; set; }

    public required string Recipient { get; set; }
    
    public required NotificationType NotificationType { get; set; }
    
    public required int ResultCount { get; set; }
}