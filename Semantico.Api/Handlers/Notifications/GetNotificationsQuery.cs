using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Notifications;

public class GetNotificationsQuery : IRequestHandler<GetNotificationsRequest, GetNotificationsResponse>
{
    private readonly SemanticoContext _context;

    public GetNotificationsQuery(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetNotificationsResponse> Handle(GetNotificationsRequest request, CancellationToken cancellation)
    {
        var notifications = await _context.Notifications
            .WhereIf(request.NotificationId.HasValue, x => x.Id == request.NotificationId)
            .WhereIf(request.QueryId.HasValue, x => x.QueryId == request.QueryId)
            .Select(x =>
                new GetNotificationsResponseListData
                {
                    NotificationsId = x.Id,
                    NotificationType = x.NotificationType,
                    Value = x.Value,
                    QueryId = x.QueryId
                })
            .ToListAsync(cancellation);

        return new GetNotificationsResponse
        {
            Notifications = notifications
        };
    }
}

public class GetNotificationsRequest : IRequest<GetNotificationsResponse>
{
    public int? NotificationId { get; set; }

    public int? QueryId { get; set; }
}

public class GetNotificationsResponse
{
    public required List<GetNotificationsResponseListData> Notifications { get; set; } = new();
}

public class GetNotificationsResponseListData
{
    public required int NotificationsId { get; set; }

    public required string Value { get; set; }

    public required int QueryId { get; set; }

    public required NotificationType NotificationType { get; set; }
}