using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Who may curate grounding, as the project authorizer decides it for the curation routes — for the lists that show it per
/// row, and for a relationship a caller creates (stored verified only when its creator may curate): a project's own
/// grounding (its learned patterns, its documentation, a patch to one of its documentation sections) needs
/// <see cref="ProjectAction.Curate"/> in the project; a data source's shared grounding (its relationships, a patch to a
/// column or table description) needs Write where the caller acts and Curate in every live project linking the source
/// (<see cref="LinkingProjects"/>), or <see cref="BeaconPermissions.DataSourceAdmin"/> with Write where it acts.
/// </summary>
internal static class GroundingCuration
{
    /// <summary>Whether the caller may curate the grounding <paramref name="projectId"/> owns.</summary>
    public static bool MayCurateProject(ProjectAccessScope scope, int projectId)
    {
        return scope.Allows(projectId, ProjectAction.Curate);
    }

    /// <summary>
    /// What the caller may curate of the shared grounding of <paramref name="dataSourceIds"/>. One database command (none for
    /// an Admin or without ids); the caller's permissions are read only when membership is not enough for every source.
    /// </summary>
    public static async Task<SharedSourceCuration> OfSharedSourcesAsync(
        BeaconContext context,
        ProjectAccessScope scope,
        IBeaconPermissionAccessor permissionAccessor,
        IReadOnlyCollection<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        var ids = dataSourceIds
            .Distinct()
            .ToList();
        if (scope.IsUnrestricted || ids.Count == 0)
        {
            return new SharedSourceCuration(scope, Array.Empty<int>().ToLookup(x => x), new HashSet<int>(), false);
        }

        var linking = await LinkingProjects.OfAsync(context, ids, cancellationToken);
        var byMembership = ids
            .Where(x => LinkingProjects.AllowEvery(scope, linking[x].ToList(), ProjectAction.Curate))
            .ToHashSet();
        var isDataSourceAdmin = byMembership.Count < ids.Count
            && (await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.DataSourceAdmin);

        return new SharedSourceCuration(scope, linking, byMembership, isDataSourceAdmin);
    }
}

/// <summary>What one caller may curate of the shared grounding of the data sources <see cref="GroundingCuration"/> read.</summary>
internal sealed class SharedSourceCuration(
    ProjectAccessScope scope,
    ILookup<int, int> linking,
    IReadOnlySet<int> byMembership,
    bool isDataSourceAdmin)
{
    /// <summary>
    /// Whether the caller may curate the source's shared grounding, acting where it may (<paramref name="writesWhereItActs"/>:
    /// Write on the resource it changes): Curate in every live project linking the source, or a DataSourceAdmin.
    /// </summary>
    public bool Allows(int dataSourceId, bool writesWhereItActs)
    {
        return scope.IsUnrestricted || (writesWhereItActs && (byMembership.Contains(dataSourceId) || isDataSourceAdmin));
    }

    /// <summary>Whether the caller may write the data source itself: through a live project linking it, or openly.</summary>
    public bool WritesSource(int dataSourceId)
    {
        return scope.IsUnrestricted
            || scope.For(ProjectAction.Write).ReachesOpenly(dataSourceId)
            || linking[dataSourceId].Any(x => scope.Allows(x, ProjectAction.Write));
    }
}
