using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Embed;

namespace Beacon.Core.Authorization;

/// <summary>
/// Resolves the request's <see cref="BeaconActor"/> from its principal the way <see cref="BeaconUserLookup"/> names the
/// user: an API key by its owner's <c>Users.Id</c>, a mapped MCP caller by its provisioned <c>Users.Id</c>, every other
/// principal by <c>NameIdentifier</c> to <c>Users.ExternalId</c>. The one user lookup also reads the user's live Admin
/// standing and role level, which the project access evaluator decides by. Resolved once per scope. Work without a
/// request is the owner it runs as in a scope set to run as the owner of scheduled work (<see cref="IRunAsScope"/>),
/// otherwise the system (<see cref="BeaconActor.OffRequest"/>) only in a scope marked as system work
/// (<see cref="ISystemWorkScope"/>); in a scope nobody marked it is nobody (no project access, fail closed), which is
/// reported as a warning.
/// </summary>
internal sealed class BeaconActorAccessor(
    IDbContextFactory<BeaconContext> contextFactory,
    ISystemWorkScope systemWork,
    IRunAsScope runAs,
    ProjectAccess.UnmarkedSystemWorkLog unmarkedSystemWork,
    IBeaconRequestPrincipal requestPrincipal,
    BeaconConfiguration configuration) : IBeaconActorAccessor
{
    private BeaconActor? _current;

    public async Task<BeaconActor> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (_current != null)
        {
            return _current;
        }

        if (!requestPrincipal.HasRequest)
        {
            // Who the scope acts as is decided now: from here on the owner it runs as can no longer be set.
            (runAs as ProjectAccess.RunAsScope)?.MarkCallerResolved();
            if (runAs.IsSet)
            {
                _current = await ResolveOwnerAsync(runAs.OwnerUserId, cancellationToken);

                return _current;
            }

            if (systemWork.IsSystemWork)
            {
                _current = BeaconActor.OffRequest;

                return _current;
            }

            unmarkedSystemWork.Report();
            _current = BeaconActor.Nobody;

            return _current;
        }

        _current = await ResolveAsync(
            requestPrincipal.User,
            contextFactory,
            cancellationToken,
            configuration.UserManagement.Enabled);

        return _current;
    }

    public async Task<BeaconActor> ResolveOwnerAsync(string? ownerUserId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(ownerUserId))
        {
            return BeaconActor.Nobody with { Kind = BeaconPrincipalKind.Owner };
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await FindUserAsync(context, new BeaconUserLookup(null, null, ownerUserId), cancellationToken);
        if (user == null)
        {
            return new BeaconActor(ownerUserId, false) { Kind = BeaconPrincipalKind.Owner };
        }

        return new BeaconActor(ownerUserId, false)
        {
            Kind = BeaconPrincipalKind.Owner,
            BeaconUserId = user.Id,
            IsActiveUser = user.IsActive,
            IsLiveAdmin = user.IsActive && user.IsLiveAdmin,
            RoleLevel = user.IsActive ? user.RoleLevel : 0
        };
    }

    /// <param name="beaconManagesUsers">
    /// Beacon user management is on. Then the user store alone decides who is an Admin: a stored user is one when its live
    /// roles say so, and a session without a stored user never is, whatever role claims a host's sign-in put on the
    /// principal. Off, a host's Admin role claim is Beacon's Admin, by design.
    /// </param>
    internal static async Task<BeaconActor> ResolveAsync(
        ClaimsPrincipal? principal,
        IDbContextFactory<BeaconContext> contextFactory,
        CancellationToken cancellationToken,
        bool beaconManagesUsers,
        TimeProvider? clock = null)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return BeaconActor.Nobody;
        }

        // An embed principal first, before anything reads it as a user or a session: it acts for its grant's creator,
        // read from the store. One that claims to be embed but does not read as Beacon's own embed principal is nobody.
        if (EmbedPrincipal.TryRead(principal, out var grantId, out var credential))
        {
            return await ResolveEmbedAsync(grantId, credential, contextFactory, clock ?? TimeProvider.System, cancellationToken);
        }

        if (EmbedPrincipal.ClaimsToBeEmbed(principal))
        {
            return BeaconActor.Nobody;
        }

        var isApiKey = BeaconUserLookup.IsApiKey(principal);
        var isMcpCaller = BeaconUserLookup.IsMcpCaller(principal);

        // A scoped caller (an API key, a mapped MCP caller) is never an Admin, whatever its owner or user is (§1.13).
        var holdsAdminRole = !beaconManagesUsers && !isApiKey && !isMcpCaller && BeaconActor.HoldsAdminRole(principal);
        var kind = isApiKey
            ? BeaconPrincipalKind.ApiKey
            : isMcpCaller
                ? BeaconPrincipalKind.McpCaller
                : BeaconPrincipalKind.Session;
        var lookup = BeaconUserLookup.Of(principal);
        if (lookup.UserId == null && lookup.ExternalId == null)
        {
            // Nothing names a user: an API key without its owner's id, a mapped MCP caller without a provisioned user,
            // or a session without a NameIdentifier.
            return isApiKey
                ? BeaconActor.Nobody
                : WithClaims(new BeaconActor(null, holdsAdminRole) { Kind = kind }, principal, isMcpCaller);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var user = await FindUserAsync(context, lookup, cancellationToken);

        if (user == null)
        {
            // An API key's owner is a stored user or nobody; a mapped MCP caller without its user acts without one. Any
            // other session without a stored user comes from a host without Beacon user management, and the id it
            // presents stands.
            if (lookup.ExternalId == null)
            {
                return isApiKey
                    ? BeaconActor.Nobody
                    : WithClaims(new BeaconActor(null, holdsAdminRole) { Kind = kind }, principal, isMcpCaller);
            }

            return new BeaconActor(lookup.ExternalId, holdsAdminRole)
            {
                Kind = BeaconPrincipalKind.Session,
                SessionIds = SessionIdsOf(principal, lookup.ExternalId)
            };
        }

        // A disabled or archived user, or a cookie session signed in with a security stamp the user no longer has (every
        // session signed out, a password reset): nobody, at once, before the session's next revalidation signs it out.
        if (!user.IsActive || (kind == BeaconPrincipalKind.Session && HasStaleStamp(principal, user.SecurityStamp)))
        {
            return BeaconActor.Nobody;
        }

        var isAdmin = !isApiKey && !isMcpCaller && (beaconManagesUsers ? user.IsLiveAdmin : holdsAdminRole);
        var actor = new BeaconActor(user.ExternalId, isAdmin)
        {
            Kind = kind == BeaconPrincipalKind.Session ? BeaconPrincipalKind.User : kind,
            SessionIds = isMcpCaller ? [] : SessionIdsOf(principal, user.ExternalId),
            IsApiKey = isApiKey,
            BeaconUserId = user.Id,
            IsActiveUser = true,
            IsLiveAdmin = user.IsLiveAdmin,
            RoleLevel = user.RoleLevel
        };

        return WithClaims(actor, principal, isApiKey || isMcpCaller);
    }

    // An embed grant (its secret, or a token issued for it) acts for the grant's creator in the grant's one project, as the
    // creator's live stored user: a revoked or expired grant, or a creator who is no longer an active user, is an embed
    // caller without a user, which the project access evaluator gives nothing. It never names a user (no UserId), so no
    // owner rule takes it for the creator, and it is never an Admin.
    private static async Task<BeaconActor> ResolveEmbedAsync(
        int grantId,
        EmbedCredential credential,
        IDbContextFactory<BeaconContext> contextFactory,
        TimeProvider clock,
        CancellationToken cancellationToken)
    {
        var refused = new BeaconActor(null, false)
        {
            Kind = BeaconPrincipalKind.Embed,
            EmbedGrantId = grantId,
            EmbedCredential = credential,
            AllowedProjectIds = []
        };

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var grant = await context.EmbedGrants
            .Where(x => x.Id == grantId)
            .Select(x =>
                new
                {
                    x.ProjectId,
                    x.CreatedByUserId,
                    x.RevokedTime,
                    x.ExpiresTime
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (grant == null
            || grant.RevokedTime != null
            || grant.ExpiresTime <= clock.GetUtcNow().UtcDateTime
            || string.IsNullOrEmpty(grant.CreatedByUserId))
        {
            return refused;
        }

        var creator = await FindUserAsync(context, new BeaconUserLookup(null, null, grant.CreatedByUserId), cancellationToken);
        if (creator is not { IsActive: true })
        {
            return refused with { AllowedProjectIds = [grant.ProjectId] };
        }

        return refused with
        {
            BeaconUserId = creator.Id,
            IsActiveUser = true,
            RoleLevel = creator.RoleLevel,
            AllowedProjectIds = [grant.ProjectId]
        };
    }

    // Archived and disabled users are read too, so their sessions never fall back to their own claim; an active row
    // wins over inactive ones with the same id. The live roles come with the same command.
    private static Task<StoredUser?> FindUserAsync(BeaconContext context, BeaconUserLookup lookup, CancellationToken cancellationToken)
    {
        return context.Users
            .IgnoreQueryFilters()
            .WhereIf(lookup.UserId != null, x => x.Id == lookup.UserId)
            .WhereIf(lookup.UserId == null, x => x.ExternalId == lookup.ExternalId)
            .OrderBy(x => x.ArchivedTime != null)
            .ThenByDescending(x => x.IsEnabled)
            .ThenBy(x => x.Id)
            .Select(x =>
                new StoredUser
                {
                    Id = x.Id,
                    ExternalId = x.ExternalId,
                    IsActive = x.IsEnabled && x.ArchivedTime == null,
                    SecurityStamp = x.SecurityStamp,
                    IsLiveAdmin = x.IsSuperAdmin || x.UserRoles.Any(y => y.Role.Name == RoleService.RoleNames.Admin),
                    RoleLevel = x.IsSuperAdmin
                        ? RoleService.RoleLevels.Admin
                        : x.UserRoles
                            .Select(y => (int?)y.Role.Level)
                            .Max() ?? 0
                })
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The session carries the stamp Beacon's sign-in recorded (BeaconClaims.SessionStamp) and the stored user has another.
    // A session without that claim (signed in before it existed, or by a host) is left to its revalidation.
    internal static bool HasStaleStamp(ClaimsPrincipal principal, string? storedStamp)
    {
        var stamp = principal
            .FindAll(BeaconClaims.SessionStamp)
            .Where(x => x.Issuer == BeaconClaims.Issuer)
            .Select(x => x.Value)
            .FirstOrDefault();

        return stamp != null && !string.Equals(stamp, storedStamp ?? string.Empty, StringComparison.Ordinal);
    }

    // The project restriction and scope a scoped caller carries.
    private static BeaconActor WithClaims(BeaconActor actor, ClaimsPrincipal principal, bool isScopedCaller)
    {
        if (!isScopedCaller)
        {
            return actor;
        }

        return actor with
        {
            AllowedProjectIds = ProjectRestriction.Of(principal),
            ScopeCeiling = ScopeOf(principal),
            CallerKind = BeaconUserLookup.IsMcpCaller(principal) ? CallerKindOf(principal) : null,
            ApiKeyId = BeaconUserLookup.IsApiKey(principal) ? ApiKeyIdOf(principal) : null
        };
    }

    private static int? ApiKeyIdOf(ClaimsPrincipal principal)
    {
        return int.TryParse(principal.FindFirst(McpCallerClaimTypes.ApiKeyId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
    }

    private static McpCallerScope? ScopeOf(ClaimsPrincipal principal)
    {
        if (principal.HasClaim(McpCallerClaimTypes.Scope, BeaconScopes.Execute))
        {
            return McpCallerScope.Execute;
        }

        return principal.HasClaim(McpCallerClaimTypes.Scope, BeaconScopes.Read)
            ? McpCallerScope.Read
            : null;
    }

    private static McpCallerKind? CallerKindOf(ClaimsPrincipal principal)
    {
        var value = principal.FindFirst(McpCallerClaimTypes.CallerKind)?.Value;

        return Enum.TryParse<McpCallerKind>(value, ignoreCase: false, out var kind) && Enum.IsDefined(kind)
            ? kind
            : null;
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

    private sealed class StoredUser
    {
        public int Id { get; init; }

        public string ExternalId { get; init; } = string.Empty;

        public bool IsActive { get; init; }

        public string? SecurityStamp { get; init; }

        public bool IsLiveAdmin { get; init; }

        public int RoleLevel { get; init; }
    }
}
