using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Beacon.Core.Handlers.Notifications;

internal sealed class GetNotificationDetailHandler(
    INotificationService notificationService,
    IHttpContextAccessor httpContextAccessor)
    : IRequestHandler<GetNotificationDetailQuery, GetNotificationDetailResult>
{
    public async Task<GetNotificationDetailResult> Handle(GetNotificationDetailQuery request, CancellationToken cancellationToken)
    {
        // A run outside the caller's projects reads as not found, like a missing one.
        var scope = StoredRunAccess.ScopeOf(httpContextAccessor.HttpContext?.User);
        var data = await notificationService.GetNotificationDetails(request.NotificationId, scope, cancellationToken);

        if (data == null)
        {
            return new GetNotificationDetailResult(null);
        }

        var entry = new NotificationDetailEntry(
            data.Id,
            data.QueryId,
            data.QueryName,
            data.SubscriptionId,
            data.RecipientName,
            data.Type,
            data.NotificationStatus,
            data.CreatedTime,
            data.SentAt,
            data.ExecutionTimeMs,
            data.ResultCount,
            data.Results);

        return new GetNotificationDetailResult(entry);
    }
}

public record GetNotificationDetailQuery(int NotificationId) : IRequest<GetNotificationDetailResult>;

public record GetNotificationDetailResult(NotificationDetailEntry? Entry);

public record NotificationDetailEntry(
    int Id,
    int QueryId,
    string QueryName,
    int SubscriptionId,
    string RecipientName,
    NotificationType Type,
    NotificationStatus Status,
    DateTime CreatedTime,
    DateTime SentAt,
    double ExecutionTimeMs,
    int? ResultCount,
    string? Results);
