using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// Whether the user a background task runs as still holds a functional permission, read live. In a request (a host without
/// a job runner) that is the request's caller (<see cref="IBeaconPermissionAccessor"/>). In the job, the scope runs as the
/// user who started the task (<see cref="IRunAsScope"/>), whom the accessor does not serve: an Admin holds every permission,
/// a stored, active user what its functional roles grant now, anyone else nothing. Identity-provider permissions live on a
/// sign-in, which a job does not have: a start admitted only by one records that on the task
/// (<see cref="GrantAtStartAsync"/>), and the job honours it only within <see cref="IdpGrantWindow"/> (and the claim's own
/// lifetime) of the start, while the stored user is still enabled and its security stamp unchanged (a role change, a
/// disable or "sign out everywhere" changes it). The system's own work (a task started by a caller without a stored user, on
/// a host without Beacon user management) holds what its start was checked for: the route's policy refused anyone else.
/// </summary>
internal sealed class BackgroundTaskRequesterPermissions(
    IBeaconRequestPrincipal requestPrincipal,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor,
    IProjectAccessEvaluator projectAccess,
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<RoleMappingOptions> roleMapping,
    TimeProvider timeProvider,
    BeaconPermissionLog log)
{
    /// <summary>How long after its start a task may rely on a permission the identity provider granted at that start.</summary>
    internal static readonly TimeSpan IdpGrantWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Where the request's caller holds <paramref name="permission"/> from, to record on the task it starts: a stored role
    /// (or Admin), else the identity provider's mapping with a hash of the user's security stamp now; null off a request, for
    /// a caller without a stored user, or one that does not hold it.
    /// </summary>
    public async Task<BackgroundTaskGrant?> GrantAtStartAsync(string permission, CancellationToken cancellationToken)
    {
        if (!requestPrincipal.HasRequest)
        {
            return null;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (!actor.IsActiveUser || actor.BeaconUserId == null)
        {
            return null;
        }

        if ((await StoredPermissionsAsync(actor, cancellationToken)).Holds(permission))
        {
            return new BackgroundTaskGrant(BackgroundTaskPermissionSources.StoredRole);
        }

        if (!(await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(permission))
        {
            return null;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var stamp = await context.Users
            .Where(x => x.Id == actor.BeaconUserId.Value)
            .Select(x => x.SecurityStamp)
            .FirstOrDefaultAsync(cancellationToken);

        return new BackgroundTaskGrant(BackgroundTaskPermissionSources.IdentityProvider, StampHash(stamp));
    }

    /// <summary>
    /// Whether the user task <paramref name="taskId"/> runs as holds <paramref name="permission"/> now: live in a request;
    /// in the job through its stored roles, or through the identity-provider grant recorded at the start while that is
    /// still honoured.
    /// </summary>
    public async Task<bool> HoldsAsync(string permission, int taskId, CancellationToken cancellationToken)
    {
        if (requestPrincipal.HasRequest)
        {
            return (await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(permission);
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.Kind != BeaconPrincipalKind.Owner)
        {
            return (await projectAccess.GetScopeAsync(cancellationToken)).IsUnrestricted;
        }

        if (!actor.IsActiveUser || actor.BeaconUserId == null)
        {
            return false;
        }

        if ((await StoredPermissionsAsync(actor, cancellationToken)).Holds(permission))
        {
            return true;
        }

        return await HoldsIdpGrantAsync(taskId, actor.BeaconUserId.Value, cancellationToken);
    }

    /// <summary>A hash of a security stamp (no stamp hashes as the empty one), so the stamp itself is never stored twice.</summary>
    internal static string StampHash(string? stamp)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stamp ?? string.Empty)));

    private async Task<BeaconPermissionSet> StoredPermissionsAsync(BeaconActor actor, CancellationToken cancellationToken)
    {
        var userId = actor.BeaconUserId!.Value;
        var roleNames = actor.IsLiveAdmin
            ? []
            : await BeaconPermissionAccessor.StoredFunctionalRolesAsync(contextFactory, userId, cancellationToken);

        return BeaconPermissionAccessor.OfStoredUser(
            actor.IsLiveAdmin,
            roleNames,
            null,
            userId,
            actor.LogId,
            roleMapping.Value,
            timeProvider,
            log);
    }

    // The start was admitted by the identity provider's mapping: honoured only shortly after it, for the same enabled user
    // with the same security stamp, while the mapping is still on.
    private async Task<bool> HoldsIdpGrantAsync(int taskId, int userId, CancellationToken cancellationToken)
    {
        var idpRoles = roleMapping.Value.IdpRoles;
        if (!idpRoles.Enabled)
        {
            return false;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var task = await context.BackgroundTasks
            .AsNoTracking()
            .Where(x => x.Id == taskId)
            .Where(x => x.RequestedByUserId == userId)
            .Where(x => x.PermissionSource == BackgroundTaskPermissionSources.IdentityProvider)
            .Select(x =>
                new
                {
                    x.CreatedTime,
                    x.RequesterStampHash,
                    Requester = context.Users
                        .Where(y => y.Id == userId)
                        .Select(y =>
                            new
                            {
                                y.IsEnabled,
                                y.SecurityStamp
                            })
                        .FirstOrDefault()
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (task?.Requester == null || !task.Requester.IsEnabled || task.RequesterStampHash == null)
        {
            return false;
        }

        var claimLifetime = TimeSpan.FromMinutes(idpRoles.ClaimLifetimeMinutes);
        var window = claimLifetime < IdpGrantWindow ? claimLifetime : IdpGrantWindow;
        if (timeProvider.GetUtcNow().UtcDateTime - task.CreatedTime > window)
        {
            return false;
        }

        return string.Equals(task.RequesterStampHash, StampHash(task.Requester.SecurityStamp), StringComparison.Ordinal);
    }
}
