using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The rule for a resource addressed by the data sources it reads (a query's steps, a run, a contract's or AI actor's
/// source): it is reachable within a <see cref="ProjectSet"/> through one project of the set that links every one of
/// them, or through the openly reachable sources when every one of them is. Such a resource has no home project: any
/// project in the set holding all of its sources grants it. An empty source set (a query without steps) is no project's
/// and not open either: it is reachable only where the set reaches everything (the owner of a query without steps is
/// decided by its caller, see <see cref="ProjectSet.ReachesStepLessQueryOf"/>).
/// </summary>
internal static class ProjectSourceFootprint
{
    public static async Task<bool> AllowsAsync(
        BeaconContext context,
        ProjectSet set,
        IReadOnlyCollection<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        if (set.Ids == null)
        {
            return true;
        }

        var sources = dataSourceIds
            .Distinct()
            .ToList();
        if (sources.Count == 0)
        {
            return false;
        }

        if (set.IncludesOpen && sources.All(x => !set.NotOpenDataSourceIds.Contains(x)))
        {
            return true;
        }

        if (set.Ids.Count == 0)
        {
            return false;
        }

        return await ProjectsHoldingAll(context, set.Ids, sources).AnyAsync(cancellationToken);
    }

    /// <summary>
    /// Whether each of <paramref name="sourceSets"/> is reachable within <paramref name="set"/>, decided as
    /// <see cref="AllowsAsync"/> decides one (an empty set is not), with at most one read of the links for all of them.
    /// </summary>
    public static async Task<IReadOnlyList<bool>> AllowEachAsync(
        BeaconContext context,
        ProjectSet set,
        IReadOnlyList<IReadOnlyCollection<int>> sourceSets,
        CancellationToken cancellationToken)
    {
        if (set.Ids == null)
        {
            return sourceSets
                .Select(_ => true)
                .ToList();
        }

        var openly = sourceSets
            .Select(x => x.Count > 0 && set.IncludesOpen && x.All(y => !set.NotOpenDataSourceIds.Contains(y)))
            .ToList();
        var pending = sourceSets
            .Where((_, i) => !openly[i])
            .SelectMany(x => x)
            .Distinct()
            .ToList();
        if (pending.Count == 0 || set.Ids.Count == 0)
        {
            return openly;
        }

        var projectIds = set.Ids.ToList();
        var links = await context.ProjectDataSources
            .Where(x => projectIds.Contains(x.ProjectId))
            .Where(x => pending.Contains(x.DataSourceId))
            .Select(x =>
                new
                {
                    x.ProjectId,
                    x.DataSourceId
                })
            .ToListAsync(cancellationToken);
        var holdings = links
            .GroupBy(x => x.ProjectId)
            .Select(x => x
                .Select(y => y.DataSourceId)
                .ToHashSet())
            .ToList();

        return sourceSets
            .Select((x, i) => openly[i] || (x.Count > 0 && holdings.Any(y => x.All(y.Contains))))
            .ToList();
    }

    /// <summary>The projects whose links hold every one of <paramref name="dataSourceIds"/> (archived projects included).</summary>
    internal static IQueryable<int> HoldersOf(BeaconContext context, IReadOnlyList<int> dataSourceIds)
    {
        var count = dataSourceIds.Count;

        return context.ProjectDataSources
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .GroupBy(x => x.ProjectId)
            .Where(x => x
                .Select(y => y.DataSourceId)
                .Distinct()
                .Count() == count)
            .Select(x => x.Key);
    }

    /// <summary>The projects among <paramref name="projectIds"/> whose links hold every one of <paramref name="dataSourceIds"/>.</summary>
    internal static IQueryable<int> ProjectsHoldingAll(
        BeaconContext context,
        IReadOnlyList<int> projectIds,
        IReadOnlyList<int> dataSourceIds)
    {
        var count = dataSourceIds.Count;

        return context.ProjectDataSources
            .Where(x => projectIds.Contains(x.ProjectId))
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .GroupBy(x => x.ProjectId)
            .Where(x => x
                .Select(y => y.DataSourceId)
                .Distinct()
                .Count() == count)
            .Select(x => x.Key);
    }
}
