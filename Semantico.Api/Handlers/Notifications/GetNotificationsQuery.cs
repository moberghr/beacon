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
    public int? NotificationId { get; init; }

    public int? QueryId { get; init; }
}

public class GetNotificationsResponse
{
    public required List<GetNotificationsResponseListData> Notifications { get; init; }
}

public class GetNotificationsResponseListData
{
    public required int NotificationsId { get; init; }

    public required string Value { get; init; }

    public required int QueryId { get; init; }

    public required NotificationType NotificationType { get; init; }
}