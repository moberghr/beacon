using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Notifications;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class NotificationsEndpoints
{
    public static RouteGroupBuilder MapNotificationsEndpoints(this RouteGroupBuilder group)
    {
        var notifications = group.MapGroup("/notifications").WithTags("Notifications");

        notifications.MapGet("/", (
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromQuery] NotificationStatus? status,
                [FromQuery] int? subscriptionId,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetNotificationsQuery(page ?? 0, pageSize ?? 100, status, subscriptionId), ct))
            .WithName("GetNotifications");

        notifications.MapGet("/{id:int}", async Task<Results<Ok<GetNotificationDetailResult>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetNotificationDetailQuery(id), ct);
            return result.Entry is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        }).WithName("GetNotificationDetail");

        return group;
    }

    /// <summary>Stub for future mark-read / dismiss endpoints.</summary>
    public static RouteGroupBuilder MapNotificationActionEndpoints(this RouteGroupBuilder group) => group;
}
