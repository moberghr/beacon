using Beacon.Core.Data;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourcesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IDataSourceProviderFactory providers,
    DataSourceTransportStatus transportStatus,
    IBeaconPermissionAccessor permissionAccessor,
    IBeaconActorAccessor actorAccessor,
    BeaconConfiguration configuration)
    : IRequestHandler<GetDataSourcesQuery, PagedList<DataSourceEntry>>
{
    public async Task<PagedList<DataSourceEntry>> Handle(GetDataSourcesQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        var shownProject = DataSourceProjects.Shown(readable);
        var otherProject = DataSourceProjects.Other(readable);
        var countsMigrationJobs = scope.IsUnrestricted;
        var visibleRequest = await DataSourceProjects.VisibleApprovalAsync(configuration, permissionAccessor, actorAccessor, cancellationToken);
        var coversConnections = configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.DataSourceConnection);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var readableStep = DataSourceProjects.ReadableStep(context, readable);

        // No redundant `ArchivedTime == null` here: the global soft-delete filter already applies it, and
        // combining the two on EF Core 9 / Npgsql collapses the predicate to WHERE FALSE. A source's project chips
        // name only the projects the caller may read, and count the rest.
        var page = await context.DataSources
            .WhereSourceAccessible(context, readable, x => x.Id)
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!))
            .WhereIf(request.DatabaseOnly == true, x => x.DatabaseEngineType != null)
            .WhereIf(request.MigrationTargets.HasValue, x => x.AllowMigrationWrites == request.MigrationTargets)
            .Select(x =>
                new DataSourceEntry
                {
                    Id = x.Id,
                    Name = x.Name,
                    DataSourceType = x.DataSourceType.ToString(),
                    DatabaseEngineType = x.DatabaseEngineType.HasValue ? x.DatabaseEngineType.Value.ToString() : null,
                    QueryCount = x.QuerySteps
                        .AsQueryable()
                        .Where(y => y.Query.ArchivedTime == null)
                        .Where(readableStep)
                        .Select(y => y.QueryId)
                        .Distinct()
                        .Count(),
                    MigrationJobsCount = countsMigrationJobs
                        ? context.MigrationJobs
                            .Where(y => y.DataSourceId == x.Id || y.DestinationDataSourceId == x.Id)
                            .Count()
                        : 0,
                    MetadataLoadingEnabled = x.MetadataLoadingEnabled,
                    OpenWhenUnlinked = x.OpenWhenUnlinked,
                    AllowMigrationWrites = x.AllowMigrationWrites,
                    DisabledTime = x.DisabledTime,
                    Projects = context.ProjectDataSources
                        .Where(y => y.DataSourceId == x.Id)
                        .Where(shownProject)
                        .Select(y =>
                            new DataSourceProjectChip
                            {
                                Id = y.ProjectId,
                                Name = y.Project.Name
                            })
                        .ToList(),
                    OtherProjectCount = context.Projects
                        .Where(y => y.DataSources.Any(z => z.DataSourceId == x.Id))
                        .Count(otherProject),
                    AwaitsFirstApproval = coversConnections
                        ? x.ApprovedTargetHash == DataSourceApprovalDefinition.Held
                        : false,
                    Approval = visibleRequest != null
                        ? context.ChangeApprovalRequests
                            .Where(y => y.Kind == ChangeApprovalKind.DataSourceConnection)
                            .Where(y => y.Status == ApprovalStatus.Pending)
                            .Where(y => y.ResourceId == x.Id)
                            .Where(visibleRequest)
                            .OrderByDescending(y => y.Id)
                            .Select(y =>
                                new DataSourceApproval
                                {
                                    RequestId = y.Id,
                                    Kind = y.Kind,
                                    RequestedTime = y.CreatedTime,
                                    AwaitsFirstApproval = x.ApprovedTargetHash == DataSourceApprovalDefinition.Held
                                })
                            .FirstOrDefault()
                        : null
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "name");

        var pageIds = page.Items
            .Select(x => x.Id)
            .ToList();
        var transportVerified = await transportStatus.VerifiedAsync(pageIds, cancellationToken);

        return page.Map(x => DataSourceProjects.WithReadOnlyEnforcement(x, providers) with { TransportVerified = transportVerified.GetValueOrDefault(x.Id) });
    }
}

/// <summary>Alphabetical unless <c>sort</c> says otherwise; <c>search</c> matches the name — pickers send it as the user types.</summary>
public record GetDataSourcesQuery : ListRequest, IRequest<PagedList<DataSourceEntry>>, IProjectFilteredRequest
{
    public string? Search { get; init; }

    /// <summary>Only sources with a database engine (SQL targets), e.g. for migrations.</summary>
    public bool? DatabaseOnly { get; init; }

    /// <summary>
    /// True: only migration write targets (a migration's destination picker); false: only the sources Beacon reads (a
    /// migration's source picker); unset: both.
    /// </summary>
    public bool? MigrationTargets { get; init; }
}

public record DataSourceEntry
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;

    public string DataSourceType { get; init; } = string.Empty;

    public string? DatabaseEngineType { get; init; }

    /// <summary>The live queries reading the source that the caller may read.</summary>
    public int QueryCount { get; init; }

    /// <summary>The migration jobs reading or writing the source; counted for Admins only (0 for anyone else).</summary>
    public int MigrationJobsCount { get; init; }

    public bool MetadataLoadingEnabled { get; init; }

    /// <summary>Whether the source stays openly reachable while no project links it (sources created since projects can be restricted do not).</summary>
    public bool OpenWhenUnlinked { get; init; }

    /// <summary>
    /// Whether an Admin marked the source as a migration write target: migrations may write to it, and Beacon never reads
    /// it, so its login may hold the write grants a migration needs. Every other source should use a login that can only
    /// read.
    /// </summary>
    public bool AllowMigrationWrites { get; init; }

    /// <summary>
    /// When a DataSourceAdmin disabled the source; null while it is enabled. A disabled source makes no connection: what
    /// reads it fails until it is enabled again.
    /// </summary>
    public DateTime? DisabledTime { get; init; }

    /// <summary>Whether the source is disabled (<see cref="DisabledTime"/> is set).</summary>
    public bool Disabled => DisabledTime != null;

    /// <summary>The live projects linking the source that the caller may read.</summary>
    public List<DataSourceProjectChip> Projects { get; init; } = [];

    /// <summary>How many other live projects link the source.</summary>
    public int OtherProjectCount { get; init; }

    /// <summary>
    /// True when the engine itself refuses writes in Beacon's read-only session (PostgreSQL, MySQL); false when the parser
    /// gates are Beacon's only read-only check, so the source must be connected with a login that can only read (unless it
    /// is a migration write target, which Beacon never reads); null for a source that runs no SQL.
    /// </summary>
    public bool? DatabaseReadOnlyEnforced { get; init; }

    /// <summary>
    /// True when the source's connection authenticates the server over TLS, as its connector checks it; false when it does
    /// not ("transport not verified": the source keeps working until its connection is replaced, unless the host refuses
    /// such sources); null when its connection data cannot be read.
    /// </summary>
    public bool? TransportVerified { get; init; }

    /// <summary>
    /// What the edit form sends back unchanged, and whether the host application manages the source (then it cannot be
    /// edited, disabled or deleted here). Set by the by-id read only; null in the list. Never the connection.
    /// </summary>
    public DataSourceEditSettings? EditSettings { get; init; }

    /// <summary>
    /// The change of the source's connection that waits for a second person's approval, while the change approval workflow
    /// covers data-source connections: shown to an Approver, and to the person who submitted it; null for everyone else and
    /// when nothing waits. While it waits, the source connects to nothing.
    /// </summary>
    public DataSourceApproval? Approval { get; init; }

    /// <summary>
    /// True while the change approval workflow covers data-source connections and the source was created under it and never
    /// approved, a request waiting or not (a rejected or withdrawn first request leaves none): it connects to nothing until
    /// its connection is saved again and approved. Shown to everyone who reads the source; false otherwise.
    /// </summary>
    public bool AwaitsFirstApproval { get; init; }
}

/// <summary>A data source's pending connection change, as its badge and banner show it.</summary>
public record DataSourceApproval
{
    /// <summary>The pending change request, which an Approver opens to review it.</summary>
    public int RequestId { get; init; }

    public ChangeApprovalKind Kind { get; init; }

    /// <summary>When the change was submitted.</summary>
    public DateTime RequestedTime { get; init; }

    /// <summary>
    /// True for a source created under the workflow that was never approved: it has never connected. False for a change
    /// of a source that was approved before: it connects again once the change is approved, or once its approved connection
    /// is saved back.
    /// </summary>
    public bool AwaitsFirstApproval { get; init; }
}

/// <summary>A data source's stored metadata-loading options, and whether the host application manages it.</summary>
public record DataSourceEditSettings
{
    public bool HostManaged { get; init; }

    public int MetadataMaxTables { get; init; }

    public int MetadataMaxColumnsPerTable { get; init; }

    public bool MetadataLoadTableNamesOnly { get; init; }

    /// <summary>Comma-separated schema names, as stored; null when none.</summary>
    public string? MetadataExcludeSchemas { get; init; }

    /// <summary>Comma-separated schema names, as stored; null when none.</summary>
    public string? MetadataIncludeSchemas { get; init; }
}

/// <summary>A project linking a data source.</summary>
public record DataSourceProjectChip
{
    public int Id { get; init; }

    public string Name { get; init; } = string.Empty;
}

/// <summary>
/// Which of a data source's projects a caller is shown by name, and which only counted; and which of the queries reading
/// it are counted.
/// </summary>
internal static class DataSourceProjects
{
    /// <summary>The entry with <see cref="DataSourceEntry.DatabaseReadOnlyEnforced"/> filled in from its provider.</summary>
    public static DataSourceEntry WithReadOnlyEnforcement(DataSourceEntry entry, IDataSourceProviderFactory providers)
    {
        if (!Enum.TryParse<DataSourceType>(entry.DataSourceType, out var type))
        {
            return entry;
        }

        DatabaseEngineType? engine = Enum.TryParse<DatabaseEngineType>(entry.DatabaseEngineType, out var parsed) ? parsed : null;

        return entry with { DatabaseReadOnlyEnforced = DataSourceReadOnlyEnforcement.Of(providers, type, engine) };
    }

    /// <summary>A step of a query the caller may read (one project in the set holds all its steps' sources).</summary>
    public static Expression<Func<QueryStep, bool>> ReadableStep(BeaconContext context, ProjectSet readable)
    {
        return ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<QueryStep>(
            context,
            readable,
            x => x.Query.Steps.Select(y => y.DataSourceId));
    }

    /// <summary>
    /// The pending connection changes the caller is shown (<see cref="DataSourceEntry.Approval"/>): every one to an Approver
    /// (an Admin holds the permission), otherwise only those the caller submitted. Null, so none is read, while the workflow
    /// does not cover data-source connections and for a caller who is neither an Approver nor has an id.
    /// </summary>
    public static async Task<Expression<Func<ChangeApprovalRequest, bool>>?> VisibleApprovalAsync(
        BeaconConfiguration configuration,
        IBeaconPermissionAccessor permissionAccessor,
        IBeaconActorAccessor actorAccessor,
        CancellationToken cancellationToken)
    {
        if (!configuration.ApprovalWorkflow.Covers(ChangeApprovalKind.DataSourceConnection))
        {
            return null;
        }

        if ((await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.Approver))
        {
            return x => true;
        }

        var requesterId = (await actorAccessor.GetCurrentAsync(cancellationToken)).UserId;

        if (requesterId == null)
        {
            return null;
        }

        return x => x.RequestedByUserId == requesterId;
    }

    public static Expression<Func<ProjectDataSource, bool>> Shown(ProjectSet readable)
    {
        if (readable.Ids == null)
        {
            return x => true;
        }

        var ids = readable.Ids.ToList();

        return x => ids.Contains(x.ProjectId);
    }

    public static Expression<Func<Project, bool>> Other(ProjectSet readable)
    {
        if (readable.Ids == null)
        {
            return x => false;
        }

        var ids = readable.Ids.ToList();

        return x => !ids.Contains(x.Id);
    }
}
