using Beacon.Core.Handlers.Subscriptions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class SubscriptionsEndpoints
{
    public static RouteGroupBuilder MapSubscriptionsEndpoints(this RouteGroupBuilder group)
    {
        var subs = group.MapGroup("/subscriptions").WithTags("Subscriptions");

        subs.MapGet("/", ([FromQuery] string? search, IMediator m, CancellationToken ct) =>
                m.Send(new GetSubscriptionsQuery(search), ct))
            .WithName("GetSubscriptions");

        subs.MapPost("/", (CreateSubscriptionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateSubscription");

        subs.MapGet("/{id:int}", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetSubscriptionDetailQuery(id), ct))
            .WithName("GetSubscriptionDetail");

        subs.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteSubscriptionCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteSubscription");

        subs.MapPost("/{id:int}/sla", async (int id, [FromBody] SetSubscriptionSlaBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SetSubscriptionSlaCommand(id, body.SlaHours), ct);
            return TypedResults.NoContent();
        }).WithName("SetSubscriptionSla");

        subs.MapPost("/{id:int}/execute", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new TestSubscriptionCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("TestSubscription");

        subs.MapPost("/{id:int}/recipients", async (int id, [FromBody] AddSubscriptionRecipientsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new AddSubscriptionRecipientsCommand(id, body.RecipientIds), ct);
            return TypedResults.NoContent();
        }).WithName("AddSubscriptionRecipients");

        subs.MapDelete("/{id:int}/recipients/{recipientId:int}", async (int id, int recipientId, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RemoveSubscriptionRecipientCommand(id, recipientId), ct);
            return TypedResults.NoContent();
        }).WithName("RemoveSubscriptionRecipient");

        subs.MapGet("/{id:int}/anomaly-chart", (int id, [FromQuery] int? days, IMediator m, CancellationToken ct) =>
                m.Send(new GetSubscriptionAnomalyChartQuery(id, days ?? 30), ct))
            .WithName("GetSubscriptionAnomalyChart");

        return group;
    }
}

internal sealed record SetSubscriptionSlaBody(int? SlaHours);
internal sealed record AddSubscriptionRecipientsBody(List<int> RecipientIds);
