using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// What project access is decided from, read as one database command per caller and request: every project, live and
/// archived, with whether it is restricted and the caller's membership role in it; and the data sources that are not
/// openly reachable. Both lists are small: the projects, and the restricted or new data sources.
/// </summary>
internal sealed class ProjectAccessSnapshot
{
    private const int ProjectRowKind = 1;
    private const int NotOpenDataSourceRowKind = 2;

    private ProjectAccessSnapshot(IReadOnlyList<ProjectAccessRow> projects, IReadOnlyList<int> notOpenDataSourceIds)
    {
        Projects = projects;
        NotOpenDataSourceIds = notOpenDataSourceIds;
    }

    public IReadOnlyList<ProjectAccessRow> Projects { get; }

    /// <summary>
    /// The data sources outside the openly reachable set: linked to projects but to no live open one (an archived
    /// project's link counts as a link), or linked to none while <c>OpenWhenUnlinked</c> is off. Archived data sources
    /// are included.
    /// </summary>
    public IReadOnlyList<int> NotOpenDataSourceIds { get; }

    /// <summary>True when no project is restricted or archived and every data source is openly reachable.</summary>
    public bool IsAllOpen => NotOpenDataSourceIds.Count == 0 && Projects.All(x => !x.RequiresMembership && !x.IsArchived);

    public static async Task<ProjectAccessSnapshot> ReadAsync(
        BeaconContext context,
        int? memberUserId,
        CancellationToken cancellationToken)
    {
        var rows = await Query(context, memberUserId).ToListAsync(cancellationToken);

        var projects = rows
            .Where(x => x.Kind == ProjectRowKind)
            .Select(x => new ProjectAccessRow(x.Id, x.RequiresMembership, x.IsArchived, x.Role))
            .ToList();
        var notOpenDataSourceIds = rows
            .Where(x => x.Kind == NotOpenDataSourceRowKind)
            .Select(x => x.Id)
            .Order()
            .ToList();

        return new ProjectAccessSnapshot(projects, notOpenDataSourceIds);
    }

    /// <summary>The snapshot command: the project rows and the not-openly-reachable data source rows in one UNION ALL.</summary>
    internal static IQueryable<SnapshotRow> Query(BeaconContext context, int? memberUserId)
    {
        var projects = context.Projects
            .IgnoreQueryFilters()
            .Select(x =>
                new SnapshotRow
                {
                    Kind = ProjectRowKind,
                    Id = x.Id,
                    RequiresMembership = x.RequiresMembership,
                    IsArchived = x.ArchivedTime != null,
                    Role = context.ProjectMembers
                        .Where(y => y.ProjectId == x.Id)
                        .Where(y => y.UserId == memberUserId)
                        .Select(y => (ProjectRole?)y.Role)
                        .FirstOrDefault()
                });

        var notOpenDataSources = context.DataSources
            .IgnoreQueryFilters()
            .Where(x => !context.ProjectDataSources
                .Where(y => y.DataSourceId == x.Id)
                .Where(y => !y.Project.RequiresMembership)
                .Any(y => y.Project.ArchivedTime == null))
            .Where(x => !x.OpenWhenUnlinked || context.ProjectDataSources.Any(y => y.DataSourceId == x.Id))
            .Select(x =>
                new SnapshotRow
                {
                    Kind = NotOpenDataSourceRowKind,
                    Id = x.Id,
                    RequiresMembership = false,
                    IsArchived = false,
                    Role = null
                });

        return projects.Concat(notOpenDataSources);
    }

    internal sealed class SnapshotRow
    {
        public int Kind { get; init; }

        public int Id { get; init; }

        public bool RequiresMembership { get; init; }

        public bool IsArchived { get; init; }

        public ProjectRole? Role { get; init; }
    }
}

/// <summary>One project in a <see cref="ProjectAccessSnapshot"/>, with the caller's membership role in it (if any).</summary>
internal sealed record ProjectAccessRow(int Id, bool RequiresMembership, bool IsArchived, ProjectRole? MemberRole);
