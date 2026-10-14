using Microsoft.EntityFrameworkCore;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization;

/// <summary>
/// How many active users hold both permissions of a conflicting pair through their stored functional roles (roles
/// assigned before separation of duties was turned on): with it on, they hold neither permission of the pair until an
/// Admin changes their roles. Read once at startup, for the log.
/// </summary>
internal static class SeparationOfDutiesReport
{
    public static async Task<int> CountUsersWithConflictsAsync(
        IDbContextFactory<BeaconContext> contextFactory,
        RoleMappingOptions mapping,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await context.UserRoles
            .Where(x => x.Role.IsSystemRole)
            .Where(x => x.Role.Level == RoleService.RoleLevels.Functional)
            .Where(x => x.User.IsEnabled)
            .Where(x => x.User.ArchivedTime == null)
            .Where(x => !x.User.IsSuperAdmin)
            .Select(x =>
                new
                {
                    x.UserId,
                    RoleName = x.Role.Name
                })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.UserId)
            .Count(x => BeaconPermissions.FirstConflict(mapping.PermissionsOfRoles(x.Select(y => y.RoleName)).ToList()) != null);
    }
}
