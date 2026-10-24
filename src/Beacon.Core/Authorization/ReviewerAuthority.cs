using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Beacon.Core.Authorization;

/// <summary>
/// A reviewer's authority read again where a decision is made: inside the decision's own transaction, after the lock of
/// what it decides, from the user store and never from the request's cached accessors (<see cref="IBeaconActorAccessor"/>,
/// <see cref="IBeaconPermissionAccessor"/>), so a reviewer disabled, archived, signed out everywhere or stripped of a role
/// while the decision waited for that lock does not decide.
/// </summary>
internal interface IReviewerAuthority
{
    /// <summary>
    /// Takes the stored reviewer's user row in <paramref name="context"/>'s open transaction (a no-op update, held until
    /// the transaction ends: a role change, a disable or an archive of the reviewer, which all write that row, commits
    /// before the decision reads it or after the decision commits, never in between) and reads what the reviewer holds now,
    /// as <see cref="IBeaconPermissionAccessor"/> derives it. A user that is gone, disabled, archived or signed in with a
    /// security stamp it no longer has is inactive and holds nothing. Null for a reviewer without a stored user (a session
    /// on a host without Beacon user management), whose permissions come from its claims alone and were checked already.
    /// </summary>
    Task<LiveReviewer?> ReadLockedAsync(BeaconContext context, BeaconActor reviewer, CancellationToken cancellationToken);
}

/// <summary>A reviewer as the user store holds it now: whether it is an active user, and the permissions it holds.</summary>
internal sealed record LiveReviewer(bool IsActive, BeaconPermissionSet Permissions)
{
    public static LiveReviewer Inactive { get; } = new(false, BeaconPermissionSet.None);
}

internal sealed class ReviewerAuthority(
    IOptions<RoleMappingOptions> roleMapping,
    TimeProvider timeProvider,
    BeaconPermissionLog log,
    IBeaconRequestPrincipal requestPrincipal) : IReviewerAuthority
{
    public async Task<LiveReviewer?> ReadLockedAsync(BeaconContext context, BeaconActor reviewer, CancellationToken cancellationToken)
    {
        if (reviewer.Kind != BeaconPrincipalKind.User || reviewer.BeaconUserId is not { } userId)
        {
            return null;
        }

        // An archived user matches no row (the soft-delete filter); one deleted since the request was read neither.
        var locked = await context.Users
            .Where(x => x.Id == userId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.SecurityStamp, y => y.SecurityStamp), cancellationToken);
        if (locked == 0)
        {
            return LiveReviewer.Inactive;
        }

        var user = await context.Users
            .Where(x => x.Id == userId)
            .Select(x =>
                new StoredReviewer
                {
                    IsActive = x.IsEnabled && x.ArchivedTime == null,
                    SecurityStamp = x.SecurityStamp,
                    IsLiveAdmin = x.IsSuperAdmin || x.UserRoles.Any(y => y.Role.Name == RoleService.RoleNames.Admin),
                    FunctionalRoles = x.UserRoles
                        .Where(y => y.Role.IsSystemRole)
                        .Where(y => y.Role.Level == RoleService.RoleLevels.Functional)
                        .Select(y => y.Role.Name)
                        .ToList()
                })
            .FirstOrDefaultAsync(cancellationToken);
        var principal = requestPrincipal.User;
        if (user is not { IsActive: true } || (principal != null && BeaconActorAccessor.HasStaleStamp(principal, user.SecurityStamp)))
        {
            return LiveReviewer.Inactive;
        }

        var permissions = BeaconPermissionAccessor.OfStoredUser(
            user.IsLiveAdmin,
            user.FunctionalRoles,
            principal,
            userId,
            reviewer.LogId,
            roleMapping.Value,
            timeProvider,
            log);

        return new LiveReviewer(true, permissions);
    }

    private sealed class StoredReviewer
    {
        public bool IsActive { get; init; }

        public string? SecurityStamp { get; init; }

        public bool IsLiveAdmin { get; init; }

        public List<string> FunctionalRoles { get; init; } = [];
    }
}
