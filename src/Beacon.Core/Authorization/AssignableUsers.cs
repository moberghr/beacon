using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization;

/// <summary>
/// Who a resource (a task's assignee, a data contract's or AI actor's owner) may be given to: with Beacon user
/// management, an existing, enabled user. A host without it keeps no users, and a task may then be assigned to any id
/// its sessions present.
/// </summary>
internal static class AssignableUsers
{
    public static async Task<bool> IsAssignableAsync(
        BeaconContext context,
        BeaconConfiguration configuration,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!configuration.UserManagement.Enabled)
        {
            return true;
        }

        return await context.Users
            .Where(x => x.ExternalId == userId)
            .Where(x => x.IsEnabled)
            .Where(x => x.ArchivedTime == null)
            .AnyAsync(cancellationToken);
    }

    /// <summary>The <c>Users.ExternalId</c> resources store for the enabled user with this id, or null.</summary>
    public static async Task<string?> ExternalIdOfActiveUserAsync(
        BeaconContext context,
        int userId,
        CancellationToken cancellationToken)
    {
        return await context.Users
            .Where(x => x.Id == userId)
            .Where(x => x.IsEnabled)
            .Where(x => x.ArchivedTime == null)
            .Select(x => x.ExternalId)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
