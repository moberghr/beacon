using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;

namespace Semantico.Api.Handlers.Notifications;

public class GetNotificationsCommand : IRequestHandler<GetNotificationsRequest, List<GetNotificationsResponse>>
{
    private readonly SemanticoContext _context;

    public GetNotificationsCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<List<GetNotificationsResponse>> Handle(GetNotificationsRequest request, CancellationToken cancellation)
    {
        var notifications = await _context.Notifications
            .WhereIf(request.NotificationId.HasValue, x => x.Id == request.NotificationId)
            .WhereIf(request.QueryId.HasValue, x => x.QueryId == request.QueryId)
            .Select(x => new GetNotificationsResponse
            {
                NotificationsId = x.Id,
                NotificationType = x.NotificationType,
                Query = x.Query,
                Value = x.Value
            })
            .ToListAsync();

        return notifications;
    }
}

public class GetNotificationsRequest : IRequest<List<GetNotificationsResponse>>
{
    public int? QueryId { get; set; }

    public int? NotificationId { get; set; }
}

public class GetNotificationsResponse
{
    public int NotificationsId { get; set; }

    public string Value { get; set; } = string.Empty;

    public int QueryId { get; set; }

    public NotificationType NotificationType { get; set; }

    public Query Query { get; set; } = null!;
}