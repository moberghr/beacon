using Beacon.Core.Data;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using System.Linq.Expressions;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourcesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IDataSourceProviderFactory providers)
    : IRequestHandler<GetDataSourcesQuery, PagedList<DataSourceEntry>>
{
    public async Task<PagedList<DataSourceEntry>> Handle(GetDataSourcesQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        var shownProject = DataSourceProjects.Shown(readable);
        var otherProject = DataSourceProjects.Other(readable);
        var countsMigrationJobs = scope.IsUnrestricted;
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
                        .Count(otherProject)
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "name");

        return page.Map(x => DataSourceProjects.WithReadOnlyEnforcement(x, providers));
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
