using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>Claims an actor's next think cycle so that only one host or job run starts it per cooldown.</summary>
public enum ThinkClaimResult
{
    Claimed,
    Held,
    Missing
}

public interface IAiActorThinkClaim
{
    /// <summary>
    /// Sets the actor's last think time to <paramref name="now"/> when it is unset or before <paramref name="threshold"/>;
    /// <see cref="ThinkClaimResult.Held"/> when another cycle holds the claim and <see cref="ThinkClaimResult.Missing"/> when
    /// the actor no longer exists (archived).
    /// </summary>
    Task<ThinkClaimResult> TryClaimAsync(int actorId, DateTime now, DateTime threshold, CancellationToken cancellationToken);
}

internal sealed class EfAiActorThinkClaim(IDbContextFactory<BeaconContext> contextFactory) : IAiActorThinkClaim
{
    public async Task<ThinkClaimResult> TryClaimAsync(int actorId, DateTime now, DateTime threshold, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var claimed = await ClaimQuery(context, actorId, threshold)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastThinkTime, now), cancellationToken);
        if (claimed > 0)
        {
            return ThinkClaimResult.Claimed;
        }

        var exists = await context.AiActors
            .Where(x => x.Id == actorId)
            .AnyAsync(cancellationToken);

        return exists ? ThinkClaimResult.Held : ThinkClaimResult.Missing;
    }

    internal static IQueryable<Core.Data.Entities.AiActor> ClaimQuery(BeaconContext context, int actorId, DateTime threshold)
    {
        return context.AiActors
            .Where(x => x.Id == actorId)
            .Where(x => x.LastThinkTime == null || x.LastThinkTime <= threshold);
    }
}
