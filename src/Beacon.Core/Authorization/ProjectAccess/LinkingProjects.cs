using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The rule for changing state a data source shares with every project that links it — its metadata, relationships and
/// descriptions, which ground each of those projects' prompts: the action is needed in every live project linking the
/// source. An action no open project grants (<see cref="ProjectAction.Curate"/>) is never granted for a source no live
/// project links; only a DataSourceAdmin or an Admin changes that one. The project authorizer and the lists that tell a
/// caller what it may curate decide with this one rule.
/// </summary>
internal static class LinkingProjects
{
    /// <summary>The live projects linking each of <paramref name="dataSourceIds"/>, by data source: one database command.</summary>
    public static async Task<ILookup<int, int>> OfAsync(
        BeaconContext context,
        IReadOnlyCollection<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        if (dataSourceIds.Count == 0)
        {
            return Array.Empty<int>().ToLookup(x => x, x => x);
        }

        var links = await context.ProjectDataSources
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Where(x => x.Project.ArchivedTime == null)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.ProjectId
                })
            .ToListAsync(cancellationToken);

        return links.ToLookup(x => x.DataSourceId, x => x.ProjectId);
    }

    /// <summary>
    /// Whether <paramref name="action"/> is allowed in every one of <paramref name="linkingProjectIds"/> (the live projects
    /// linking a source). With none, only where the action reaches the openly reachable sources: never for Curate.
    /// </summary>
    public static bool AllowEvery(ProjectAccessScope scope, IReadOnlyCollection<int> linkingProjectIds, ProjectAction action)
    {
        if (scope.IsUnrestricted)
        {
            return true;
        }

        if (linkingProjectIds.Count == 0)
        {
            return scope.For(action).IncludesOpen;
        }

        return linkingProjectIds.All(x => scope.Allows(x, action));
    }
}
