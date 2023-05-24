using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Handlers.Notifications;

public class GetNotificationsCommand : IRequestHandler<GetNotificationsRequest, GetNotificationsResponse>
{
    private readonly SemanticoContext _context;

    public GetNotificationsCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<GetNotificationsResponse> Handle(GetNotificationsRequest request)
    {
        var notifications = await _context.Notifications
            .Select(x => new GetNotificationsResponseListData
            {
                NotificationsId = x.Id,
                NotificationType = x.NotificationType,
                Query = x.Query,
                Value = x.Value
            })
            .ToListAsync();

    }
}

public class GetNotificationsRequest : IRequest<GetNotificationsResponse>
{ }

public class GetNotificationsResponse
{
    List<GetNotificationsResponseListData> Notifications = new();
}
public class GetNotificationsResponseListData
{
    public int NotificationsId { get; set; }
    public string Value { get; set; } = string.Empty;
    public int QueryId { get; set; }
    public NotificationType NotificationType { get; set; }
    public Query Query { get; set; } = null!;
}

