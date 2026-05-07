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

        return group;
    }
}
