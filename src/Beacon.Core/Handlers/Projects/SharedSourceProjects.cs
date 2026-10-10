using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.Projects;

/// <summary>
/// The other live projects, open or restricted, that link any of a project's data sources, with the sources they share
/// and their active ProjectAdmins: who else reaches those sources when the project is restricted or its links change.
/// For an Admin's view of the project's access (the project detail and the access route).
/// </summary>
internal static class SharedSourceProjects
{
    public static async Task<List<SharedSourceProjectEntry>> ReadAsync(
        BeaconContext context,
        int projectId,
        bool requireMembershipEverywhere,
        CancellationToken cancellationToken)
    {
        var sourceIds = context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.DataSourceId);

        var rows = await context.Projects
            .Where(x => x.Id != projectId)
            .Where(x => x.DataSources.Any(y => sourceIds.Contains(y.DataSourceId)))
            .OrderBy(x => x.Name)
            .ThenBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Name,
                    x.RequiresMembership,
                    SharedDataSourceIds = x.DataSources
                        .Where(y => sourceIds.Contains(y.DataSourceId))
                        .Select(y => y.DataSourceId)
                        .OrderBy(y => y)
                        .ToList(),
                    ProjectAdmins = x.Members
                        .Where(y => y.Role == ProjectRole.ProjectAdmin)
                        .Where(y => y.User.IsEnabled)
                        .Where(y => y.User.ArchivedTime == null)
                        .OrderBy(y => y.UserId)
                        .Select(y =>
                            new
                            {
                                y.UserId,
                                y.User.DisplayName
                            })
                        .ToList()
                })
            .ToListAsync(cancellationToken);

        return rows
            .Select(x =>
                new SharedSourceProjectEntry(
                    x.Id,
                    x.Name,
                    x.RequiresMembership || requireMembershipEverywhere,
                    x.SharedDataSourceIds,
                    x.ProjectAdmins
                        .Select(y => UserLabel.Of(y.UserId, y.DisplayName))
                        .ToList()))
            .ToList();
    }
}

/// <summary>
/// Another live project linking some of a project's data sources: whether it is restricted, the data sources it shares
/// with the project, and the display names of its active ProjectAdmins.
/// </summary>
public record SharedSourceProjectEntry(
    int ProjectId,
    string Name,
    bool RequiresMembership,
    List<int> SharedDataSourceIds,
    List<string> ProjectAdminNames);
