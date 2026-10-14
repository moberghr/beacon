using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.HostData;
using Beacon.Core.Models;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The rules for linking existing data sources into a project, on every link path (creating a project, linking into
/// one). A data source must exist and be readable by the caller (404 otherwise, the same answer for both); a host's data
/// source links only into the project the host declares for it (the same host key); and anyone but an Admin or a
/// DataSourceAdmin links only sources a live open project already holds, so a link never carries a restricted project's
/// data into another project (403 otherwise). A DataSourceAdmin links any existing source and gains no membership by it;
/// one it cannot read only into a restricted project it is not a member of, so the link never lets it read the source
/// (403 otherwise, also for a new project, whose creator becomes its member or which is created open). Callers read the
/// links inside the transaction that writes them, so the check and the write see the same links.
/// </summary>
internal static class ProjectDataSourceLinks
{
    /// <summary>The data sources' current state and links, as <see cref="Check"/> decides them.</summary>
    public static async Task<IReadOnlyList<LinkCandidate>> ReadAsync(
        BeaconContext context,
        IReadOnlyCollection<int> dataSourceIds,
        CancellationToken cancellationToken)
    {
        var sources = await context.DataSources
            .Where(x => dataSourceIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.HostManagedKey,
                    x.OpenWhenUnlinked
                })
            .ToListAsync(cancellationToken);

        // Links to archived projects are read too: they count as links, and never make a source open.
        var links = await LinksQuery(context, dataSourceIds).ToListAsync(cancellationToken);

        return sources
            .Select(x =>
                new LinkCandidate(
                    x.Id,
                    x.HostManagedKey,
                    x.OpenWhenUnlinked,
                    links
                        .Where(y => y.DataSourceId == x.Id)
                        .Select(y => new SourceLink(y.ProjectId, y.IsLiveOpen))
                        .ToList()))
            .ToList();
    }

    /// <summary>
    /// The host key of the project a host declares a host data source for (by the data source's host key); null when this
    /// host does not declare the data source.
    /// </summary>
    public static Func<string, string?> HostProjectKeys(IHostDataSourceRegistry registry)
    {
        return dataSourceHostKey => registry.Registrations
            .Where(x => x.Key == dataSourceHostKey)
            .Select(x => HostProjectResolver.KeyFor(x.ProjectName))
            .FirstOrDefault();
    }

    public const string UnreadableSourceMessage =
        "A data source you cannot read can be linked only into a restricted project you are not a member of; an Admin can link it anywhere.";

    /// <summary>
    /// Refuses the link of any of <paramref name="dataSourceIds"/> into the target (whose host key is
    /// <paramref name="targetHostKey"/>, null for a project created in Beacon): 404 for a data source that is missing or
    /// the caller may not read, 403 for one it may read but not link. <paramref name="asDataSourceAdmin"/>: the caller
    /// holds <see cref="BeaconPermissions.DataSourceAdmin"/> and links any existing source, one it cannot read only when
    /// <paramref name="targetHiddenFromCaller"/> (the target is a restricted project the caller is not a member of).
    /// </summary>
    public static void Check(
        ProjectAccessScope scope,
        IReadOnlyCollection<int> dataSourceIds,
        IReadOnlyList<LinkCandidate> candidates,
        string? targetHostKey,
        Func<string, string?> hostProjectKeyOf,
        bool requireMembershipEverywhere,
        BeaconActor actor,
        ILogger logger,
        bool asDataSourceAdmin = false,
        bool targetHiddenFromCaller = false)
    {
        var linksAnySource = scope.IsUnrestricted || asDataSourceAdmin;
        var readable = scope.For(ProjectAction.Read);
        foreach (var dataSourceId in dataSourceIds)
        {
            var candidate = candidates
                .Where(x => x.DataSourceId == dataSourceId)
                .FirstOrDefault();
            var isReadable = candidate != null && (scope.IsUnrestricted || IsReadable(candidate, readable, requireMembershipEverywhere));
            if (candidate == null || (!linksAnySource && !isReadable))
            {
                AccessRefusal.Hidden(
                    logger,
                    actor,
                    "data source link",
                    dataSourceId,
                    ProjectAction.ManageMembers,
                    candidate == null ? AccessRefusal.Missing : AccessRefusal.NotReadable);

                throw new BeaconNotFoundException($"Data source {dataSourceId} not found.");
            }

            // A DataSourceAdmin's link must not let it, or the members it works with, read a source it cannot read.
            if (!isReadable && !targetHiddenFromCaller)
            {
                throw AccessRefusal.Of(logger, actor, "data source link", dataSourceId, UnreadableSourceMessage);
            }

            if (candidate.HostManagedKey != null && (targetHostKey == null || hostProjectKeyOf(candidate.HostManagedKey) != targetHostKey))
            {
                throw AccessRefusal.Of(logger, actor, "data source link", dataSourceId, "A host's data source can be linked only to the host's project.");
            }

            if (!linksAnySource && (requireMembershipEverywhere || !candidate.Links.Any(x => x.IsLiveOpen)))
            {
                throw AccessRefusal.Of(logger, actor, "data source link", dataSourceId, "Only an Admin or a DataSourceAdmin can link a data source that no open project holds.");
            }
        }
    }

    /// <summary>
    /// The consistency rule for rows that name both a project and a data source (glossary terms, golden cases): the data
    /// source must be linked to the project, so a project's grounding never names another project's source. A row
    /// without a data source passes.
    /// </summary>
    public static async Task EnsureLinkedAsync(
        BeaconContext context,
        int projectId,
        int? dataSourceId,
        CancellationToken cancellationToken)
    {
        if (dataSourceId == null)
        {
            return;
        }

        var isLinked = await context.ProjectDataSources
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.DataSourceId == dataSourceId.Value)
            .AnyAsync(cancellationToken);
        if (!isLinked)
        {
            throw new InvalidOperationException($"Data source {dataSourceId} is not linked to project {projectId}.");
        }
    }

    /// <summary>The links of <paramref name="dataSourceIds"/>, with whether each project is live and open.</summary>
    internal static IQueryable<SourceLinkRow> LinksQuery(BeaconContext context, IReadOnlyCollection<int> dataSourceIds)
    {
        return context.ProjectDataSources
            .IgnoreQueryFilters()
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Select(x =>
                new SourceLinkRow
                {
                    DataSourceId = x.DataSourceId,
                    ProjectId = x.ProjectId,
                    IsLiveOpen = !x.Project.RequiresMembership && x.Project.ArchivedTime == null
                });
    }

    // Readable as a resource addressed by its data source: through a project the caller may read that links it, or as
    // an openly reachable source (linked to a live open project, or unlinked and left open).
    private static bool IsReadable(LinkCandidate candidate, ProjectSet readable, bool requireMembershipEverywhere)
    {
        if (readable.Ids == null)
        {
            return true;
        }

        if (candidate.Links.Any(x => readable.Ids.Contains(x.ProjectId)))
        {
            return true;
        }

        var isOpen = !requireMembershipEverywhere
            && (candidate.Links.Any(x => x.IsLiveOpen) || (candidate.Links.Count == 0 && candidate.OpenWhenUnlinked));

        return readable.IncludesOpen && isOpen;
    }

    internal sealed class SourceLinkRow
    {
        public int DataSourceId { get; init; }

        public int ProjectId { get; init; }

        public bool IsLiveOpen { get; init; }
    }
}

/// <summary>
/// A data source considered for linking: the key of the host that owns it (null for an ordinary source), whether it
/// stays open unlinked, its links.
/// </summary>
internal sealed record LinkCandidate(int DataSourceId, string? HostManagedKey, bool OpenWhenUnlinked, IReadOnlyList<SourceLink> Links);

/// <summary>One project link of a data source, and whether that project is live and open.</summary>
internal sealed record SourceLink(int ProjectId, bool IsLiveOpen);
