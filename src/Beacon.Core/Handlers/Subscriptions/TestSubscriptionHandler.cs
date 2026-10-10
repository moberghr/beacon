using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Worker;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class TestSubscriptionHandler(
    IJobService jobService,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    ILogger<TestSubscriptionHandler> logger)
    : IRequestHandler<TestSubscriptionCommand>
{
    public async Task Handle(TestSubscriptionCommand request, CancellationToken cancellationToken)
    {
        await EnsureMayRunAsync(request.SubscriptionId, cancellationToken);

        await jobService.ExecuteQuery(request.SubscriptionId, cancellationToken);
    }

    // A subscription an AI actor manages is run on demand by the actor's creator or an Admin, like the actor itself. An
    // actor that is gone leaves it to Admins.
    private async Task EnsureMayRunAsync(int subscriptionId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    CreatedByUserId = x.AiActor != null ? x.AiActor.CreatedByUserId : null
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription?.AiActorId == null)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!actor.IsOwnerOrAdmin(subscription.CreatedByUserId))
        {
            throw AccessRefusal.Of(
                logger,
                actor,
                "subscription",
                subscriptionId,
                "Only the AI actor's creator or an Admin can run the subscriptions it manages.");
        }
    }
}

/// <summary>
/// Runs a subscription now and delivers its notifications. A subscription managed by an AI actor is run by the actor's
/// creator or an Admin only.
/// </summary>
public record TestSubscriptionCommand(int SubscriptionId) : IRequest;
