using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Authorization;

/// <summary>
/// Resolves the request's <see cref="BeaconActor"/> from its principal the way <see cref="BeaconUserLookup"/> names the
/// user: an API key by its owner's <c>Users.Id</c>, every other principal by <c>NameIdentifier</c> to
/// <c>Users.ExternalId</c>. Resolved once per scope.
/// </summary>
internal sealed class BeaconActorAccessor(
    IHttpContextAccessor httpContextAccessor,
    IDbContextFactory<BeaconContext> contextFactory) : IBeaconActorAccessor
{
    private BeaconActor? _current;

    public async Task<BeaconActor> GetCurrentAsync(CancellationToken cancellationToken)
    {
        return _current ??= await ResolveAsync(httpContextAccessor.HttpContext?.User, contextFactory, cancellationToken);
    }

    internal static async Task<BeaconActor> ResolveAsync(
        ClaimsPrincipal? principal,
        IDbContextFactory<BeaconContext> contextFactory,
        CancellationToken cancellationToken)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return BeaconActor.Nobody;
        }

        var holdsAdminRole = BeaconActor.HoldsAdminRole(principal);
        var lookup = BeaconUserLookup.Of(principal);
        if (lookup.UserId == null && lookup.ExternalId == null)
        {
            // Nothing names a user: an API key without its owner's id, or a session without a NameIdentifier.
            return new BeaconActor(null, holdsAdminRole);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Archived and disabled users are read too, so their sessions never fall back to their own claim; an active row
        // wins over inactive ones with the same id.
        var user = await context.Users
            .IgnoreQueryFilters()
            .WhereIf(lookup.UserId != null, x => x.Id == lookup.UserId)
            .WhereIf(lookup.UserId == null, x => x.ExternalId == lookup.ExternalId)
            .OrderBy(x => x.ArchivedTime != null)
            .ThenByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.ExternalId,
                    IsActive = x.IsEnabled && x.ArchivedTime == null
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (user == null)
        {
            // An API key's owner is a stored user or nobody. Any other session without a stored user comes from a host
            // without Beacon user management, and the id it presents stands.
            return lookup.ExternalId == null
                ? BeaconActor.Nobody
                : new BeaconActor(lookup.ExternalId, holdsAdminRole) { SessionIds = SessionIdsOf(principal, lookup.ExternalId) };
        }

        if (!user.IsActive)
        {
            return BeaconActor.Nobody;
        }

        return new BeaconActor(user.ExternalId, holdsAdminRole)
        {
            SessionIds = SessionIdsOf(principal, user.ExternalId),
            IsApiKey = BeaconUserLookup.IsApiKey(principal)
        };
    }

    private static IReadOnlyList<string> SessionIdsOf(ClaimsPrincipal principal, string userId)
    {
        return new[]
            {
                principal.FindFirst(BeaconClaims.UserId)?.Value,
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            }
            .OfType<string>()
            .Where(x => x.Length > 0)
            .Where(x => x != userId)
            .Distinct()
            .ToList();
    }
}
