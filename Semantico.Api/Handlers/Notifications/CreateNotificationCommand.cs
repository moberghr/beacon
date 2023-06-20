using MediatR;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Handlers.Notifications;

public class CreateNotificationCommand : IRequestHandler<CreateNotificationRequest, CreateNotificationResponse>
{
    private readonly SemanticoContext _context;

    public CreateNotificationCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<CreateNotificationResponse> Handle(CreateNotificationRequest request, CancellationToken cancellationToken)
    {
        var notification = new Notification
        {
            Value = request.Value,
            QueryId = request.QueryId,
            NotificationType = request.NotificationType
        };

        _context.Notifications.Add(notification);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class CreateNotificationRequest : IRequest<CreateNotificationResponse>
{
    public string Value { get; init; } = string.Empty;

    public int QueryId { get; init; }

    public NotificationType NotificationType { get; init; }
}

public class CreateNotificationResponse
{
}