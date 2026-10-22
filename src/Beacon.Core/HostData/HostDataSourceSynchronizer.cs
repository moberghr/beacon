using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Beacon.Core.HostData;

/// <summary>Result of syncing one host data source.</summary>
internal sealed record HostSyncOutcome(int DataSourceId, bool Created, bool MetadataRewritten, int TableCount);

/// <summary>
/// Upserts the project, data source, project link and metadata for every <c>ExposeDbContext</c> registration,
/// keyed by <c>DataSource.HostManagedKey</c>. The project comes from <see cref="IHostProjectResolver"/> (by
/// <c>Project.HostManagedKey</c>, never by name), and the data source is linked to that project only — a link to any
/// other project (a renamed <c>ProjectName</c>, a hand-made link) is removed. Once every source is synced, their read-only
/// logins are checked for write access all at once (<see cref="IHostReadOnlyLoginCheck"/>, each bounded by its timeout, a
/// Warning only, never failing the sync). Called by the host at startup through
/// <c>SyncBeaconHostDataSourcesAsync</c> — deliberately not a hosted service (§2.15).
/// </summary>
internal sealed class HostDataSourceSynchronizer(
    IDbContextFactory<BeaconContext> contextFactory,
    IHostProjectResolver projectResolver,
    IHostDataSourceRegistry registry,
    IEncryptionService encryptionService,
    IConfiguration configuration,
    ISchemaRelationshipSyncService relationshipSyncService,
    ISchemaGraphService schemaGraphService,
    IMemoryCache cache,
    IProjectMembershipSource membershipSource,
    IOptions<ProjectAuthorizationOptions> authorizationOptions,
    IHostReadOnlyLoginCheck loginCheck,
    ILogger<HostDataSourceSynchronizer> logger)
{
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        var synced = new List<(DataSource DataSource, IReadOnlyList<HostExposedTable> Tables)>();
        foreach (var registration in registry.Registrations)
        {
            var (_, dataSource, tables) = await SyncSourceAsync(registration, cancellationToken);
            synced.Add((dataSource, tables));
        }

        // In parallel after the sync: startup waits for the slowest check (each bounded), not for their sum.
        await Task.WhenAll(synced.Select(x => CheckLoginAsync(x.DataSource, x.Tables, cancellationToken)));
    }

    public async Task<HostSyncOutcome> SyncAsync(HostDataSourceRegistration registration, CancellationToken cancellationToken)
    {
        var (outcome, _, _) = await SyncSourceAsync(registration, cancellationToken);

        return outcome;
    }

    private async Task<(HostSyncOutcome Outcome, DataSource DataSource, IReadOnlyList<HostExposedTable> Tables)> SyncSourceAsync(
        HostDataSourceRegistration registration,
        CancellationToken cancellationToken)
    {
        // Startup validation: the read-only login must be configured. Only the entry NAME is ever reported.
        DataSourceConnectionResolver.ResolveConfigured(configuration, registration.ConnectionStringName);

        var snapshot = registry.GetSnapshot(registration.Key)
            ?? throw new InvalidOperationException($"Host data source '{registration.Name}' is not registered.");

        var projectId = await projectResolver.EnsureAsync(registration.ProjectName, registration.Options.Description, cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .IgnoreQueryFilters()
            .Where(x => x.HostManagedKey == registration.Key)
            .FirstOrDefaultAsync(cancellationToken);

        var created = dataSource == null;
        dataSource ??= new DataSource
        {
            Name = registration.Name,
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = EncryptReference(registration),
            HostManagedKey = registration.Key
        };

        ApplyDataSourceFields(dataSource, registration, snapshot);

        var links = created
            ? []
            : await context.ProjectDataSources
                .Where(x => x.DataSourceId == dataSource.Id)
                .ToListAsync(cancellationToken);

        var linked = links.Any(x => x.ProjectId == projectId);
        var staleLinks = links
            .Where(x => x.ProjectId != projectId)
            .ToList();
        var mayRelink = linked || staleLinks.Count == 0
            || await CarryRestrictionAsync(context, registration, projectId, staleLinks, cancellationToken);

        var rewriteMetadata = created || dataSource.HostModelHash != snapshot.ModelHash;
        HostMetadataMergeResult? merge = null;
        if (rewriteMetadata)
        {
            var existing = created
                ? []
                : await context.DatabaseMetadata
                    .IgnoreQueryFilters()
                    .Include(x => x.Columns)
                    .Include(x => x.Indexes)
                    .Where(x => x.DataSourceId == dataSource.Id)
                    .ToListAsync(cancellationToken);

            merge = HostMetadataMerger.Merge(dataSource, existing, snapshot.Tables, DateTime.UtcNow);
            dataSource.HostModelHash = snapshot.ModelHash;
        }

        if (created)
        {
            context.DataSources.Add(dataSource);
        }

        if (mayRelink && staleLinks.Count > 0)
        {
            // Only the declared host project may reach the host database (e.g. after ProjectName changed).
            context.ProjectDataSources.RemoveRange(staleLinks);
            logger.LogWarning(
                "Host data source {DataSourceName} was linked to {StaleLinkCount} other project(s); those links were removed.",
                registration.Name,
                staleLinks.Count);
        }

        if (mayRelink && !linked)
        {
            context.ProjectDataSources.Add(new ProjectDataSource { ProjectId = projectId, DataSource = dataSource });
        }

        if (merge != null)
        {
            context.ColumnMetadata.RemoveRange(merge.RemovedColumns);
            context.IndexMetadata.RemoveRange(merge.RemovedIndexes);
            context.DatabaseMetadata.AddRange(merge.AddedTables);
        }

        await context.SaveChangesAsync(cancellationToken);

        if (rewriteMetadata)
        {
            await SyncRelationshipsAsync(dataSource.Id, cancellationToken);
            cache.Remove(DatabaseMetadataService.GetCacheKey(dataSource.Id));
            schemaGraphService.Invalidate(dataSource.Id);
        }

        LogOutcome(registration, snapshot, merge, created);

        return (new HostSyncOutcome(dataSource.Id, created, rewriteMetadata, snapshot.Tables.Count), dataSource, snapshot.Policy.Tables);
    }

    // Beacon trusts the configured login to be read-only: a login that can write is a startup Warning, never a refusal, and
    // a check that fails is a Warning too — the sources are synced already.
    private async Task CheckLoginAsync(DataSource dataSource, IReadOnlyList<HostExposedTable> tables, CancellationToken cancellationToken)
    {
        try
        {
            await loginCheck.WarnIfWritableAsync(dataSource, tables, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(
                "Host data source {DataSourceId}: could not verify that its read-only login cannot write ({ExceptionType})",
                dataSource.Id,
                ex.GetType().Name);
        }
    }

    // The source leaves the projects it was linked to for the host project. When one of them is restricted, the host
    // project becomes restricted, so the move never opens the source, and takes the members of the live restricted
    // project (an archived project grants nothing, so its members are never carried over). When that is impossible (the
    // host manages membership, several live restricted projects held it, or the host project already has members the
    // restricted project does not, who would gain the source) the source stays where it is.
    private async Task<bool> CarryRestrictionAsync(
        BeaconContext context,
        HostDataSourceRegistration registration,
        int projectId,
        IReadOnlyList<ProjectDataSource> staleLinks,
        CancellationToken cancellationToken)
    {
        var restrictsEverywhere = authorizationOptions.Value.RequireProjectMembership;
        var leftProjectIds = staleLinks
            .Select(x => x.ProjectId)
            .Distinct()
            .ToList();
        var restricted = await context.Projects
            .IgnoreQueryFilters()
            .Where(x => leftProjectIds.Contains(x.Id))
            .Where(x => restrictsEverywhere || x.RequiresMembership)
            .Select(x =>
                new
                {
                    x.Id,
                    IsLive = x.ArchivedTime == null
                })
            .ToListAsync(cancellationToken);
        if (restricted.Count == 0)
        {
            return true;
        }

        var live = restricted
            .Where(x => x.IsLive)
            .Select(x => x.Id)
            .ToList();
        if (live.Count > 1 || !ProjectMembershipRules.IsManagedByBeacon(membershipSource))
        {
            logger.LogError(
                "Host data source {DataSourceName} was not moved to host project {ProjectId}: it is held by restricted project(s) {RestrictedProjectIds} whose access cannot be carried over ({Reason}). Link it by hand or restrict the host project.",
                registration.Name,
                projectId,
                restricted.Select(x => x.Id).ToList(),
                live.Count > 1 ? "more than one restricted project" : "project membership is not managed by Beacon");

            return false;
        }

        int? predecessorId = live.Count == 1 ? live[0] : null;
        var existingMemberIds = await context.ProjectMembers
            .Where(x => x.ProjectId == projectId)
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        var predecessorMembers = await context.ProjectMembers
            .Where(x => x.ProjectId == predecessorId)
            .Select(x =>
                new
                {
                    x.UserId,
                    x.Role
                })
            .ToListAsync(cancellationToken);

        var unrelatedMemberCount = existingMemberIds.Count(x => predecessorMembers.All(y => y.UserId != x));
        if (unrelatedMemberCount > 0)
        {
            logger.LogError(
                "Host data source {DataSourceName} was not moved to host project {ProjectId}: restricted project(s) {RestrictedProjectIds} hold it, and the host project has {UnrelatedMemberCount} member(s) the live one does not, who would gain the source. Link it by hand or align the members.",
                registration.Name,
                projectId,
                restricted.Select(x => x.Id).ToList(),
                unrelatedMemberCount);

            return false;
        }

        var hostProject = await context.Projects
            .Where(x => x.Id == projectId)
            .FirstAsync(cancellationToken);
        hostProject.RequiresMembership = true;

        var inherited = predecessorMembers
            .Where(x => !existingMemberIds.Contains(x.UserId))
            .ToList();
        foreach (var member in inherited)
        {
            context.ProjectMembers.Add(new ProjectMember { ProjectId = projectId, UserId = member.UserId, Role = member.Role });
        }

        logger.LogWarning(
            ProjectAccessEvents.AccessModeChanged,
            "Host project {ProjectId} restricted with {MemberCount} member(s) carried over from restricted project {PredecessorProjectId}, which held host data source {DataSourceName}.",
            projectId,
            inherited.Count,
            predecessorId,
            registration.Name);

        return true;
    }

    private void ApplyDataSourceFields(DataSource dataSource, HostDataSourceRegistration registration, HostExposureSnapshot snapshot)
    {
        dataSource.Unarchive();
        dataSource.Name = registration.Name;
        dataSource.DataSourceType = DataSourceType.Database;
        dataSource.DatabaseEngineType = snapshot.Engine;
        dataSource.IsReadOnly = true;
        dataSource.MetadataLoadingEnabled = true;
        dataSource.MetadataLoadTableNamesOnly = false;
        dataSource.MetadataMaxTables = 0;
        dataSource.MetadataMaxColumnsPerTable = 0;
        dataSource.MetadataIncludeSchemas = null;
        dataSource.MetadataExcludeSchemas = null;

        if (!ReferencesConnection(dataSource, registration))
        {
            dataSource.EncryptedConnectionData = EncryptReference(registration);
        }
    }

    private bool ReferencesConnection(DataSource dataSource, HostDataSourceRegistration registration)
    {
        try
        {
            var reference = HostConnectionReference.Parse(encryptionService.Decrypt(dataSource.EncryptedConnectionData));

            return reference.HostConnectionStringName == registration.ConnectionStringName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private string EncryptReference(HostDataSourceRegistration registration)
    {
        return encryptionService.Encrypt(new HostConnectionReference(registration.ConnectionStringName).ToJson());
    }

    private async Task SyncRelationshipsAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        // Relationship edges are an enrichment; a failure keeps the metadata sync (mirrors RefreshDataSourceMetadataHandler).
        try
        {
            await relationshipSyncService.SyncAsync(dataSourceId, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Schema relationship sync failed for host data source {DataSourceId}; metadata kept.", dataSourceId);
        }
    }

    private void LogOutcome(HostDataSourceRegistration registration, HostExposureSnapshot snapshot, HostMetadataMergeResult? merge, bool created)
    {
        foreach (var column in registration.Options.SecretLikeOverrides)
        {
            logger.LogWarning(
                "Host data source {DataSourceName} lifts the secret-like hard-exclude for {Column} (IncludeSecretLikeColumn).",
                registration.Name,
                column);
        }

        if (merge == null)
        {
            logger.LogInformation(
                "Host data source {DataSourceName} ({ContextType}) unchanged: {TableCount} exposed tables.",
                registration.Name,
                registration.ContextType.Name,
                snapshot.Tables.Count);

            return;
        }

        logger.LogInformation(
            "Host data source {DataSourceName} ({ContextType}) synced{Created}: {TableCount} exposed tables, {Added} added, {Updated} updated, {Archived} archived, {ExcludedColumns} excluded and {MaskedColumns} masked columns.",
            registration.Name,
            registration.ContextType.Name,
            created ? " (created)" : string.Empty,
            snapshot.Tables.Count,
            merge.AddedTables.Count,
            merge.UpdatedTables,
            merge.ArchivedTables,
            snapshot.Policy.Tables.Sum(x => x.ExcludedColumns.Count),
            snapshot.Policy.Tables.Sum(x => x.MaskedColumns.Count));
    }
}
