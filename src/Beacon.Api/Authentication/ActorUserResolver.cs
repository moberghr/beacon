using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Api.Authentication;

internal sealed class ActorUserResolver(
    IHttpContextAccessor httpContextAccessor,
    IDbContextFactory<BeaconContext> contextFactory) : IActorUserResolver
{
    public async Task<int?> ResolveActorUserIdAsync(CancellationToken cancellationToken = default)
    {
        // The user as BeaconUserLookup names it, like BeaconActor: an API key by its owner's Users.Id (its numeric
        // NameIdentifier), every other principal by NameIdentifier to Users.ExternalId.
        var lookup = BeaconUserLookup.Of(httpContextAccessor.HttpContext?.User);
        if (lookup.UserId == null && lookup.ExternalId == null)
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Users
            .WhereIf(lookup.UserId != null, x => x.Id == lookup.UserId)
            .WhereIf(lookup.UserId == null, x => x.ExternalId == lookup.ExternalId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
