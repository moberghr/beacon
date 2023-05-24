using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Api.Data;

namespace Semantico.Api.Handlers.Notifications;

public class DeleteNotificationCommand : IRequestHandler<DeleteNotificationRequest, DeleteNotificationResponse>
{
    private readonly SemanticoContext _context;

    public DeleteNotificationCommand(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<DeleteNotificationResponse> Handle(DeleteNotificationRequest request, CancellationToken cancellationToken)
    {
        var notification = await _context.Notifications
            .Where(x => x.Id == request.NotificationId)
            .FirstAsync(cancellationToken);

        _context.Notifications.Remove(notification);
        await _context.SaveChangesAsync(cancellationToken);

        return new();
    }
}

public class DeleteNotificationRequest : IRequest<DeleteNotificationResponse>
{
    public int NotificationId { get; set; }
}

public class DeleteNotificationResponse
{
}

