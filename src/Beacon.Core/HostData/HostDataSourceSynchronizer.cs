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

namespace Beacon.Core.HostData;

/// <summary>Result of syncing one host data source.</summary>
internal sealed record HostSyncOutcome(int DataSourceId, bool Created, bool MetadataRewritten, int TableCount);

/// <summary>
/// Upserts the project, data source, project link and metadata for every <c>ExposeDbContext</c> registration,
/// keyed by <c>DataSource.HostManagedKey</c>. Called by the host at startup through
/// <c>SyncBeaconHostDataSourcesAsync</c> — deliberately not a hosted service (§2.15).
/// </summary>
internal sealed class HostDataSourceSynchronizer(
    IDbContextFactory<BeaconContext> contextFactory,
    IHostDataSourceRegistry registry,
    IEncryptionService encryptionService,
    IConfiguration configuration,
    ISchemaRelationshipSyncService relationshipSyncService,
    ISchemaGraphService schemaGraphService,
    IMemoryCache cache,
    ILogger<HostDataSourceSynchronizer> logger)
{
    public async Task SyncAllAsync(CancellationToken cancellationToken)
    {
        foreach (var registration in registry.Registrations)
        {
            await SyncAsync(registration, cancellationToken);
        }
    }

    public async Task<HostSyncOutcome> SyncAsync(HostDataSourceRegistration registration, CancellationToken cancellationToken)
    {
        // Startup validation: the read-only login must be configured. Only the entry NAME is ever reported.
        DataSourceConnectionResolver.ResolveConfigured(configuration, registration.ConnectionStringName);

        var snapshot = registry.GetSnapshot(registration.Key)
            ?? throw new InvalidOperationException($"Host data source '{registration.Name}' is not registered.");

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

        var project = await context.Projects
            .Where(x => x.Name == registration.ProjectName)
            .FirstOrDefaultAsync(cancellationToken);

        var createProject = project == null;
        project ??= new Project
        {
            Name = registration.ProjectName,
            Description = registration.Options.Description
        };

        var linked = !created && !createProject && await context.ProjectDataSources
            .Where(x => x.ProjectId == project.Id)
            .Where(x => x.DataSourceId == dataSource.Id)
            .AnyAsync(cancellationToken);

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

        if (createProject)
        {
            context.Projects.Add(project);
        }

        if (!linked)
        {
            context.ProjectDataSources.Add(new ProjectDataSource { Project = project, DataSource = dataSource });
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

        return new HostSyncOutcome(dataSource.Id, created, rewriteMetadata, snapshot.Tables.Count);
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
