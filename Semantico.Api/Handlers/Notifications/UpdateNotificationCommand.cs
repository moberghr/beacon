using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Handlers.Notifications;

public class UpdateNotificationCommand : IRequestHandler<UpdateNotificationRequest, UpdateNotificationResponse>
{
    private readonly SemanticoContext _context;

    public UpdateNotificationCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<UpdateNotificationResponse> Handle(UpdateNotificationRequest request, CancellationToken cancellationToken)
    {
        var notification = await _context.Notifications
            .Where(x => x.Id == request.NotificationId)
            .FirstAsync(cancellationToken);

        notification.Value = request.Value;
        notification.NotificationType = request.NotificationType;

        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class UpdateNotificationRequest : IRequest<UpdateNotificationResponse>
{
    public int NotificationId { get; set; }

    public string Value { get; set; } = string.Empty;

    public NotificationType NotificationType { get; set; }
}

public class UpdateNotificationResponse
{
}

