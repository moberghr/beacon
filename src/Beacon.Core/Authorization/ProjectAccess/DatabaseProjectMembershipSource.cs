using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The default membership source: <c>ProjectMember</c> rows of the actor's stored, active user. Available while Beacon
/// user management is on. The evaluator reads the same rows in its one snapshot command instead of calling
/// <see cref="GetMembershipsAsync"/>.
/// </summary>
internal sealed class DatabaseProjectMembershipSource(
    BeaconConfiguration configuration,
    IDbContextFactory<BeaconContext> contextFactory) : IProjectMembershipSource
{
    public bool IsAvailable => configuration.UserManagement.Enabled;

    public async Task<IReadOnlyDictionary<int, ProjectRole>?> GetMembershipsAsync(BeaconActor actor, CancellationToken cancellationToken)
    {
        if (!IsAvailable || actor.BeaconUserId == null || !actor.IsActiveUser)
        {
            return new Dictionary<int, ProjectRole>();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.ProjectMembers
            .Where(x => x.UserId == actor.BeaconUserId)
            .ToDictionaryAsync(x => x.ProjectId, x => x.Role, cancellationToken);
    }
}
