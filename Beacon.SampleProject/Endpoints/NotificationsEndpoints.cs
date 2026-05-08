using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Notifications;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class NotificationsEndpoints
{
    public static RouteGroupBuilder MapNotificationsEndpoints(this RouteGroupBuilder group)
    {
        var notifications = group.MapGroup("/notifications").WithTags("Notifications");

        notifications.MapGet("/", async (
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromQuery] NotificationStatus? status,
                [FromQuery] int? subscriptionId,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(
                    new GetNotificationsQuery(page ?? 0, pageSize ?? 100, status, subscriptionId),
                    ct);
                return Results.Ok(result);
            })
            .WithName("GetNotifications")
            .Produces<GetNotificationsResult>(StatusCodes.Status200OK);

        notifications.MapGet("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetNotificationDetailQuery(id), ct);
                return result.Entry is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetNotificationDetail")
            .Produces<GetNotificationDetailResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        return group;
    }

    /// <summary>
    /// Stub for future mark-read / dismiss endpoints. Today the Blazor side has
    /// no such actions either. Kept as a separate Map* extension so the
    /// composition root can wire it once the actions ship.
    /// </summary>
    public static RouteGroupBuilder MapNotificationActionEndpoints(this RouteGroupBuilder group)
    {
        return group;
    }
}
