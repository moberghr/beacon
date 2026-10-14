using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Notifications;

internal sealed class GetNotificationDetailHandler(
    INotificationService notificationService,
    IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetNotificationDetailQuery, GetNotificationDetailResult>
{
    public async Task<GetNotificationDetailResult> Handle(GetNotificationDetailQuery request, CancellationToken cancellationToken)
    {
        // A run outside the caller's projects reads as not found, like a missing one.
        var scope = await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken);
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

public record GetNotificationDetailQuery(int NotificationId) : IRequest<GetNotificationDetailResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Notification(NotificationId), ProjectAction.Read)
    ];
}

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
