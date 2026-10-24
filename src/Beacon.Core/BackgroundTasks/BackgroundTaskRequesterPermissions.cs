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
/// a stored, active user what its functional roles grant now (identity-provider permissions live on a sign-in, which a job
/// does not have), anyone else nothing. The system's own work (a task started by a caller without a stored user, on a host
/// without Beacon user management) holds what its start was checked for: the route's policy refused anyone else.
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
    public async Task<bool> HoldsAsync(string permission, CancellationToken cancellationToken)
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

        var roleNames = actor.IsLiveAdmin
            ? []
            : await BeaconPermissionAccessor.StoredFunctionalRolesAsync(contextFactory, actor.BeaconUserId.Value, cancellationToken);
        var permissions = BeaconPermissionAccessor.OfStoredUser(
            actor.IsLiveAdmin,
            roleNames,
            null,
            actor.BeaconUserId.Value,
            actor.LogId,
            roleMapping.Value,
            timeProvider,
            log);

        return permissions.Holds(permission);
    }
}
