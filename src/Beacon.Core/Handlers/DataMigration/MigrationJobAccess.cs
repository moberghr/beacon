using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.DataMigration;

/// <summary>
/// What creating, running or deleting a migration job demands of a caller who is not an Admin (a MigrationOperator),
/// through project access with no bypass. A job reads its declared source and the data source of every step its
/// <c>QueryText</c> runs (<see cref="MigrationQuerySteps"/>, read the way the run reads it), with SQL of its own: Query on
/// each of them and Write on the destination (404 for one the caller may not read, 403 for one it may read but not query
/// or write), and a destination no more widely reachable than any of them (403 otherwise): every live project that links
/// the destination links each source too, and an openly reachable destination needs openly reachable sources. A
/// migration therefore never copies data to where more people can read it. A source that is also the destination widens
/// nothing, but is checked like every other. A failed run's error text is shown only to callers who may read every source
/// the job reads. Refusals are logged with ids only.
/// </summary>
internal sealed class MigrationJobAccess(
    IProjectAccessEvaluator projectAccess,
    ProjectRequestAuthorizer authorizer,
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    IOptions<ProjectAuthorizationOptions> options,
    ILogger<MigrationJobAccess> logger)
{
    public const string WiderDestinationMessage =
        "The destination is reachable by more people than the source: every project that links the destination must link the source too.";

    public const string OpenDestinationMessage =
        "The destination is openly reachable but the source is not: data would reach people who cannot read the source.";

    public const string HiddenErrorMessage =
        "The run failed. Its error is shown only to users who may read every data source the job reads.";

    /// <summary>The demands on a new job: its declared source, the steps of <paramref name="queryText"/>, its destination.</summary>
    public async Task DemandAsync(int sourceDataSourceId, string? queryText, int destinationDataSourceId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (scope.IsUnrestricted)
        {
            return;
        }

        await DemandReachAsync(MigrationQuerySteps.SourceIds(sourceDataSourceId, queryText), destinationDataSourceId, cancellationToken);
    }

    /// <summary>
    /// The demands on a stored job, as it is stored now (running it, deleting it); a missing job is left to the caller,
    /// which answers it.
    /// </summary>
    public async Task DemandForJobAsync(int migrationJobId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (scope.IsUnrestricted)
        {
            return;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var job = await context.MigrationJobs
            .Where(x => x.Id == migrationJobId)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    x.QueryText,
                    x.DestinationDataSourceId
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (job != null)
        {
            await DemandReachAsync(MigrationQuerySteps.SourceIds(job.DataSourceId, job.QueryText), job.DestinationDataSourceId, cancellationToken);
        }
    }

    /// <summary>
    /// The jobs among <paramref name="migrationJobIds"/> that read a data source outside <paramref name="readable"/>
    /// (through a step): their failed runs' error text is not shown. Nothing to read for a caller who reads everything.
    /// </summary>
    public async Task<IReadOnlySet<int>> JobsReadingUnreadableSourcesAsync(
        IReadOnlyCollection<int> migrationJobIds,
        ProjectSet readable,
        CancellationToken cancellationToken)
    {
        if (readable.Ids == null || migrationJobIds.Count == 0)
        {
            return new HashSet<int>();
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var jobs = await context.MigrationJobs
            .IgnoreQueryFilters()
            .Where(x => migrationJobIds.Contains(x.Id))
            .Select(x =>
                new
                {
                    x.Id,
                    x.DataSourceId,
                    x.QueryText
                })
            .ToListAsync(cancellationToken);
        var sourcesOf = jobs.ToDictionary(x => x.Id, x => MigrationQuerySteps.SourceIds(x.DataSourceId, x.QueryText));
        var sourceIds = sourcesOf.Values
            .SelectMany(x => x)
            .Distinct()
            .ToList();
        var readableSourceIds = (await context.DataSources
                .Where(x => sourceIds.Contains(x.Id))
                .WhereSourceAccessible(context, readable, x => x.Id)
                .Select(x => x.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        return migrationJobIds
            .Where(x => !sourcesOf.TryGetValue(x, out var sources) || sources.Any(y => !readableSourceIds.Contains(y)))
            .ToHashSet();
    }

    private async Task DemandReachAsync(IReadOnlyList<int> sourceDataSourceIds, int destinationDataSourceId, CancellationToken cancellationToken)
    {
        await authorizer.DemandAsync(
            sourceDataSourceIds
                .Select(x => new ProjectCheck(ProjectResource.DataSource(x), ProjectAction.Query))
                .Append(new ProjectCheck(ProjectResource.DataSource(destinationDataSourceId), ProjectAction.Write)),
            cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var dataSourceIds = sourceDataSourceIds
            .Append(destinationDataSourceId)
            .Distinct()
            .ToList();
        var links = await context.ProjectDataSources
            .IgnoreQueryFilters()
            .Where(x => dataSourceIds.Contains(x.DataSourceId))
            .Select(x =>
                new MigrationReach.Link(
                    x.DataSourceId,
                    x.ProjectId,
                    x.Project.ArchivedTime == null,
                    !x.Project.RequiresMembership))
            .ToListAsync(cancellationToken);
        var openWhenUnlinked = await context.DataSources
            .IgnoreQueryFilters()
            .Where(x => dataSourceIds.Contains(x.Id))
            .Where(x => x.OpenWhenUnlinked)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);

        foreach (var sourceDataSourceId in sourceDataSourceIds)
        {
            var refusal = MigrationReach.Refusal(
                sourceDataSourceId,
                destinationDataSourceId,
                links,
                openWhenUnlinked,
                options.Value.RequireProjectMembership);
            if (refusal != null)
            {
                var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

                throw AccessRefusal.Of(logger, actor, "migration destination", destinationDataSourceId, refusal);
            }
        }
    }
}

/// <summary>Whether a migration's destination is more widely reachable than its source (see <see cref="MigrationJobAccess"/>).</summary>
internal static class MigrationReach
{
    /// <summary>
    /// Why copying from <paramref name="sourceDataSourceId"/> to <paramref name="destinationDataSourceId"/> would widen
    /// who reaches the data; null when it would not. <paramref name="links"/> holds both sources' project links, archived
    /// projects included (they count as links but reach nobody); <paramref name="openWhenUnlinked"/> the ids of those left
    /// open while unlinked.
    /// </summary>
    public static string? Refusal(
        int sourceDataSourceId,
        int destinationDataSourceId,
        IReadOnlyList<Link> links,
        IReadOnlyCollection<int> openWhenUnlinked,
        bool requireMembershipEverywhere)
    {
        if (sourceDataSourceId == destinationDataSourceId)
        {
            return null;
        }

        var sourceProjects = links
            .Where(x => x.DataSourceId == sourceDataSourceId)
            .Where(x => x.IsLive)
            .Select(x => x.ProjectId)
            .ToHashSet();
        var widens = links
            .Where(x => x.DataSourceId == destinationDataSourceId)
            .Where(x => x.IsLive)
            .Any(x => !sourceProjects.Contains(x.ProjectId));
        if (widens)
        {
            return MigrationJobAccess.WiderDestinationMessage;
        }

        var destinationIsOpen = IsOpen(destinationDataSourceId, links, openWhenUnlinked, requireMembershipEverywhere);
        var sourceIsOpen = IsOpen(sourceDataSourceId, links, openWhenUnlinked, requireMembershipEverywhere);

        return destinationIsOpen && !sourceIsOpen ? MigrationJobAccess.OpenDestinationMessage : null;
    }

    // Openly reachable: linked to a live open project, or linked nowhere and left open; never with every project restricted.
    private static bool IsOpen(
        int dataSourceId,
        IReadOnlyList<Link> links,
        IReadOnlyCollection<int> openWhenUnlinked,
        bool requireMembershipEverywhere)
    {
        if (requireMembershipEverywhere)
        {
            return false;
        }

        var own = links
            .Where(x => x.DataSourceId == dataSourceId)
            .ToList();

        return own.Count == 0
            ? openWhenUnlinked.Contains(dataSourceId)
            : own.Any(x => x.IsLive && x.IsOpenProject);
    }

    /// <summary>A project link of a data source: whether the project is live (not archived) and open.</summary>
    internal sealed record Link(int DataSourceId, int ProjectId, bool IsLive, bool IsOpenProject);
}
