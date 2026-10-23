using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization;

/// <summary>
/// The default <see cref="IBeaconPermissionAccessor"/>. A stored, active user holds the permissions its functional roles
/// grant in the user store now (system roles at level 0 only; one read, only when a permission is asked for) and, while
/// identity-provider mapping is on, the permissions its single sign-on mapped from app roles
/// (<see cref="BeaconClaims.IdpPermission"/>, issued by <see cref="BeaconClaims.Issuer"/>, for
/// <c>IdpRoles:ClaimLifetimeMinutes</c> after that sign-in). An Admin now holds every permission. A session without a
/// stored user, on a host without Beacon user management, holds every permission through the Admin role claim, and what
/// its other role claims grant only with <c>Roles:HonourHostRoleClaims</c>. Anyone else holds nothing. With separation of
/// duties on, a caller holding both permissions of a conflicting pair holds neither of them.
/// </summary>
internal sealed class BeaconPermissionAccessor(
    IBeaconActorAccessor actorAccessor,
    IDbContextFactory<BeaconContext> contextFactory,
    BeaconConfiguration configuration,
    IOptions<RoleMappingOptions> roleMapping,
    TimeProvider timeProvider,
    BeaconPermissionLog log,
    IBeaconRequestPrincipal requestPrincipal) : IBeaconPermissionAccessor
{
    private BeaconPermissionSet? _current;

    public async Task<BeaconPermissionSet> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (_current != null)
        {
            return _current;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        _current = await ResolveAsync(actor, requestPrincipal.User, cancellationToken);

        return _current;
    }

    /// <summary>
    /// What a stored, active user holds from its live state: every permission as an Admin; else what its functional roles
    /// (<paramref name="functionalRoleNames"/>, system roles at level 0) grant and, while identity-provider mapping is on,
    /// what its sign-in mapped, under separation of duties. Shared with <see cref="ReviewerAuthority"/>, which reads that
    /// state again inside a decision's transaction.
    /// </summary>
    internal static BeaconPermissionSet OfStoredUser(
        bool isLiveAdmin,
        IEnumerable<string> functionalRoleNames,
        ClaimsPrincipal? principal,
        int userId,
        string? callerId,
        RoleMappingOptions mapping,
        TimeProvider timeProvider,
        BeaconPermissionLog log)
    {
        if (isLiveAdmin)
        {
            return BeaconPermissionSet.Admin;
        }

        var permissions = mapping.PermissionsOfRoles(functionalRoleNames).ToHashSet();
        if (mapping.IdpRoles.Enabled && principal != null)
        {
            permissions.UnionWith(IdpPermissions(principal, mapping, userId, timeProvider, log));
        }

        return Separated(permissions, mapping, callerId, log);
    }

    private async Task<BeaconPermissionSet> ResolveAsync(BeaconActor actor, ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        var mapping = roleMapping.Value;
        switch (actor.Kind)
        {
            case BeaconPrincipalKind.User when actor.IsActiveUser && actor.BeaconUserId != null:
                if (actor.IsLiveAdmin)
                {
                    return BeaconPermissionSet.Admin;
                }

                var roleNames = await StoredFunctionalRolesAsync(actor.BeaconUserId.Value, cancellationToken);

                return OfStoredUser(false, roleNames, principal, actor.BeaconUserId.Value, actor.LogId, mapping, timeProvider, log);

            case BeaconPrincipalKind.Session when !configuration.UserManagement.Enabled && principal != null:
                // No stored user to read: on a host without Beacon user management the Admin claim counts, as it does for
                // project access; other role claims only where the host opted in to them.
                if (BeaconActor.HoldsAdminRole(principal))
                {
                    return BeaconPermissionSet.Admin;
                }

                if (!mapping.Roles.HonourHostRoleClaims)
                {
                    return BeaconPermissionSet.None;
                }

                var claimed = mapping.PermissionsOfRoles(principal.FindAll(ClaimTypes.Role).Select(x => x.Value));

                return Separated(claimed.ToHashSet(), mapping, actor.LogId, log);

            default:
                return BeaconPermissionSet.None;
        }
    }

    // Under separation of duties a conflicting pair grants neither of its permissions, whichever way they were gained.
    private static BeaconPermissionSet Separated(HashSet<string> permissions, RoleMappingOptions mapping, string? callerId, BeaconPermissionLog log)
    {
        if (mapping.SeparationOfDuties.Enabled)
        {
            var (kept, conflicts) = BeaconPermissions.WithoutConflicts(permissions);
            if (conflicts.Count > 0)
            {
                log.ConflictDropped(callerId, conflicts);
                permissions = kept.ToHashSet();
            }
        }

        return permissions.Count == 0 ? BeaconPermissionSet.None : new BeaconPermissionSet(false, permissions);
    }

    // The permissions the sign-in mapped, when Beacon's sign-in issued them and they are still within their lifetime.
    private static List<string> IdpPermissions(ClaimsPrincipal principal, RoleMappingOptions mapping, int userId, TimeProvider timeProvider, BeaconPermissionLog log)
    {
        var mapped = principal
            .FindAll(BeaconClaims.IdpPermission)
            .Where(x => x.Issuer == BeaconClaims.Issuer)
            .Select(x => BeaconPermissions.Find(x.Value))
            .OfType<string>()
            .ToList();
        if (mapped.Count == 0)
        {
            return [];
        }

        var issuedAt = principal
            .FindAll(BeaconClaims.IdpPermissionsIssuedAt)
            .Where(x => x.Issuer == BeaconClaims.Issuer)
            .Select(x => long.TryParse(x.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ? (long?)seconds : null)
            .FirstOrDefault();
        var age = timeProvider.GetUtcNow().ToUnixTimeSeconds() - issuedAt;
        if (age == null || age > TimeSpan.FromMinutes(mapping.IdpRoles.ClaimLifetimeMinutes).TotalSeconds)
        {
            log.IdpPermissionsExpired(userId, issuedAt);

            return [];
        }

        return mapped;
    }

    private async Task<List<string>> StoredFunctionalRolesAsync(int userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.UserRoles
            .Where(x => x.UserId == userId)
            .Where(x => x.Role.IsSystemRole)
            .Where(x => x.Role.Level == RoleService.RoleLevels.Functional)
            .Select(x => x.Role.Name)
            .ToListAsync(cancellationToken);
    }
}
