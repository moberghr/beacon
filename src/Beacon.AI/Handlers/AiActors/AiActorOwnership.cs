using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;
using Beacon.Core.Data;

namespace Beacon.AI.Handlers.AiActors;

/// <summary>
/// An AI actor is paused, resumed, archived, refined, run on demand, or sent a plan revision request by its creator
/// (the caller that created it, or the user an Admin made its creator) or an Admin. An actor without a recorded creator
/// is an Admin's only. To anyone but an Admin, an actor or plan that does not exist is refused like one that is not
/// theirs. Scheduled think cycles do not pass through here. Approving and rejecting a plan are not checked here.
/// Refusals are logged at Warning with the actor and the caller's user id only; a security audit event for them belongs
/// here.
/// </summary>
internal static class AiActorOwnership
{
    private const string Refusal = "Only the AI actor's creator or an Admin can change or run it.";

    public static async Task EnsureCreatorOrAdminAsync(
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory,
        int actorId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Where(x => x.Id == actorId)
            .Select(x =>
                new
                {
                    x.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (actor == null)
        {
            throw caller.IsAdmin
                ? new InvalidOperationException($"AI actor {actorId} not found.")
                : Refuse(logger, caller, actorId);
        }

        if (!caller.IsOwnerOrAdmin(actor.CreatedByUserId))
        {
            throw Refuse(logger, caller, actorId);
        }
    }

    /// <summary>The same rule for a plan, through the actor that proposed it.</summary>
    public static async Task EnsureCreatorOrAdminOfPlanAsync(
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory,
        int planId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Where(x => x.Id == planId)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    x.AiActor.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (plan == null)
        {
            throw caller.IsAdmin
                ? new InvalidOperationException($"AI actor plan {planId} not found.")
                : Refuse(logger, caller, null);
        }

        if (!caller.IsOwnerOrAdmin(plan.CreatedByUserId))
        {
            throw Refuse(logger, caller, plan.AiActorId);
        }
    }

    private static UnauthorizedAccessException Refuse(ILogger logger, BeaconActor caller, int? actorId)
    {
        logger.LogWarning("Refused a change to {Resource} {ResourceId} by user {UserId}", "AI actor", actorId, caller.UserId);

        return new UnauthorizedAccessException(Refusal);
    }
}
