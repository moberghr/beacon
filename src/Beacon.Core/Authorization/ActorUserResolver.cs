using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization;

internal sealed class ActorUserResolver(
    IHttpContextAccessor httpContextAccessor,
    IDbContextFactory<BeaconContext> contextFactory) : IActorUserResolver
{
    public async Task<int?> ResolveActorUserIdAsync(CancellationToken cancellationToken = default)
    {
        // Match on the OIDC subject / external id claim, not IBeaconUserContext.UserId — that
        // property prefers BeaconClaims.UserId (username), which does not match Users.ExternalId.
        var externalId = httpContextAccessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(externalId))
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        return await context.Users
            .Where(x => x.ExternalId == externalId)
            .Select(x => (int?)x.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
