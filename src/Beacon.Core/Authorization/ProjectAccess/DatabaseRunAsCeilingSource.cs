using Beacon.Core.Services;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The default ceiling for an actor who is not the request's signed-in caller: the role levels its stored user holds
/// now (read with the actor). Editor or higher writes, Viewer reads, no role or no stored user grants nothing.
/// </summary>
internal sealed class DatabaseRunAsCeilingSource : IRunAsCeilingSource
{
    public Task<ProjectRole?> GetCeilingAsync(BeaconActor owner, CancellationToken cancellationToken)
    {
        return Task.FromResult(CeilingOf(owner));
    }

    private static ProjectRole? CeilingOf(BeaconActor owner)
    {
        if (owner.BeaconUserId == null || !owner.IsActiveUser)
        {
            return null;
        }

        if (owner.RoleLevel >= RoleService.RoleLevels.Editor)
        {
            return ProjectRole.Editor;
        }

        return owner.RoleLevel >= RoleService.RoleLevels.Viewer ? ProjectRole.Viewer : null;
    }
}
