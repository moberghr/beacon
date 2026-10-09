using Beacon.Core.Handlers.Subscriptions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class SubscriptionsEndpoints
{
    public static RouteGroupBuilder MapSubscriptionsEndpoints(this RouteGroupBuilder group)
    {
        var subs = group.MapGroup("/subscriptions").WithTags("Subscriptions");

        subs.MapGet("/", ([AsParameters] GetSubscriptionsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetSubscriptions");

        // Every mutating route needs the Execute scope (§1.4) on top of the write permission the group's permission
        // filter enforces: a subscription runs its query on a schedule and delivers the rows to its recipients, and
        // /execute runs and delivers at once. Attaching existing recipients is allowed to the same callers; only Admins
        // create or re-point recipients themselves (RecipientsEndpoints).
        subs.MapPost("/", (CreateSubscriptionCommand cmd, IMediator m, CancellationToken ct) => m.Send(cmd, ct))
            .WithName("CreateSubscription")
            .RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapGet("/{id:int}", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetSubscriptionDetailQuery(id), ct))
            .WithName("GetSubscriptionDetail");

        subs.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteSubscriptionCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteSubscription").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapPost("/{id:int}/reactivate", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new ReactivateSubscriptionCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("ReactivateSubscription").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapPost("/{id:int}/sla", async (int id, [FromBody] SetSubscriptionSlaBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SetSubscriptionSlaCommand(id, body.SlaHours), ct);
            return TypedResults.NoContent();
        }).WithName("SetSubscriptionSla").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapPost("/{id:int}/execute", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new TestSubscriptionCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("TestSubscription").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapPost("/{id:int}/recipients", async (int id, [FromBody] AddSubscriptionRecipientsBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new AddSubscriptionRecipientsCommand(id, body.RecipientIds), ct);
            return TypedResults.NoContent();
        }).WithName("AddSubscriptionRecipients").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapDelete("/{id:int}/recipients/{recipientId:int}", async (int id, int recipientId, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RemoveSubscriptionRecipientCommand(id, recipientId), ct);
            return TypedResults.NoContent();
        }).WithName("RemoveSubscriptionRecipient").RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        subs.MapGet("/{id:int}/anomaly-chart", (int id, [FromQuery] int? days, IMediator m, CancellationToken ct) =>
                m.Send(new GetSubscriptionAnomalyChartQuery(id, days ?? 30), ct))
            .WithName("GetSubscriptionAnomalyChart");

        return group;
    }
}

internal sealed record SetSubscriptionSlaBody(int? SlaHours);
internal sealed record AddSubscriptionRecipientsBody(List<int> RecipientIds);
