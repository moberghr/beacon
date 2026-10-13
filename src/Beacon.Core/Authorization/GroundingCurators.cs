using Beacon.Core.Data;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Authorization;

/// <summary>
/// Who curates a project's grounding, as stored — for work that runs without a caller (the learning aggregation), which
/// weighs only curators' verdicts: an enabled user who is a project admin member of the project, an Admin, or a super
/// admin. The same people <see cref="ProjectAction.Curate"/> admits on an interactive request; never an API key.
/// </summary>
public static class GroundingCurators
{
    /// <summary>
    /// Which of <paramref name="userIds"/> curate <paramref name="projectId"/>'s grounding: one database command, none
    /// without ids.
    /// </summary>
    public static async Task<IReadOnlySet<int>> AmongAsync(
        BeaconContext context,
        int projectId,
        IReadOnlyCollection<int> userIds,
        CancellationToken cancellationToken)
    {
        if (userIds.Count == 0)
        {
            return new HashSet<int>();
        }

        var curators = await context.Users
            .Where(x => userIds.Contains(x.Id))
            .Where(x => x.IsEnabled)
            .Where(x => x.IsSuperAdmin
                || x.UserRoles.Any(y => y.Role.Name == RoleService.RoleNames.Admin)
                || context.ProjectMembers
                    .Where(y => y.ProjectId == projectId)
                    .Where(y => y.UserId == x.Id)
                    .Any(y => y.Role == ProjectRole.ProjectAdmin))
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        return curators.ToHashSet();
    }
}
