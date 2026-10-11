using Microsoft.EntityFrameworkCore;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Authorization;

/// <summary>
/// The users and roles only an Admin may change, whoever else administers users: a super admin, and a holder of a
/// protected role — the Admin role, every functional role (a system role at level 0, whatever it is named now) and any
/// role named as a functional permission's role. A UserAdmin who is not an Admin changes none of their accounts, roles,
/// project memberships or API keys, and assigns none of those roles. A permission an identity provider maps for a single
/// sign-on session is not stored, so it does not make its holder protected.
/// </summary>
internal static class ProtectedUsers
{
    /// <summary>True for a protected role (see <see cref="ProtectedUsers"/>).</summary>
    public static bool IsProtectedRole(string name, bool isSystemRole, int level, RoleMappingOptions mapping)
    {
        return string.Equals(name, RoleService.RoleNames.Admin, StringComparison.OrdinalIgnoreCase)
            || (isSystemRole && level == RoleService.RoleLevels.Functional)
            || mapping.PermissionOfRole(name) != null;
    }

    /// <summary>
    /// True when user <paramref name="userId"/> (archived or disabled ones too) is a super admin or holds a protected role
    /// now; false for a user that does not exist. One read.
    /// </summary>
    public static async Task<bool> IsProtectedAsync(
        BeaconContext context,
        int userId,
        RoleMappingOptions mapping,
        CancellationToken cancellationToken)
    {
        return (await ProtectedAmongAsync(context, [userId], mapping, cancellationToken)).Count > 0;
    }

    /// <summary>
    /// The users among <paramref name="userIds"/> (archived or disabled ones too) that are super admins or hold a protected
    /// role now; users that do not exist are not among them. One read, none for no users.
    /// </summary>
    public static async Task<IReadOnlySet<int>> ProtectedAmongAsync(
        BeaconContext context,
        IReadOnlyCollection<int> userIds,
        RoleMappingOptions mapping,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<int>();
        }

        var ids = userIds
            .Distinct()
            .ToList();
        var targets = await context.Users
            .IgnoreQueryFilters()
            .Where(x => ids.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.IsSuperAdmin,
                    Roles = x.UserRoles
                        .Select(y =>
                            new
                            {
                                y.Role.Name,
                                y.Role.IsSystemRole,
                                y.Role.Level
                            })
                        .ToList()
                })
            .ToListAsync(cancellationToken);

        return targets
            .Where(x => x.IsSuperAdmin || x.Roles.Any(y => IsProtectedRole(y.Name, y.IsSystemRole, y.Level, mapping)))
            .Select(x => x.Id)
            .ToHashSet();
    }
}
