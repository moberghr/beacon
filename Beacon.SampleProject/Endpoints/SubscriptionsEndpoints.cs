using Beacon.Core.Handlers.Subscriptions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class SubscriptionsEndpoints
{
    public static RouteGroupBuilder MapSubscriptionsEndpoints(this RouteGroupBuilder group)
    {
        var subs = group.MapGroup("/subscriptions").WithTags("Subscriptions");

        subs.MapGet("/", async (
                [FromQuery] string? search,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetSubscriptionsQuery(search), ct)))
            .WithName("GetSubscriptions")
            .Produces<GetSubscriptionsResult>(StatusCodes.Status200OK);

        subs.MapPost("/", async (CreateSubscriptionCommand command, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateSubscription")
            .Produces<CreateSubscriptionResult>(StatusCodes.Status200OK);

        subs.MapDelete("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteSubscriptionCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("DeleteSubscription")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}
