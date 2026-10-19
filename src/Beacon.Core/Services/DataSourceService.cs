using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Models.Providers;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Core.Worker;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

public interface IDataSourceService
{
    Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken);

    /// <summary>
    /// Disables a data source (no connection is made with it from then on) or enables it again; recorded as who disabled it
    /// and when, with a security event. Setting the state it already has changes nothing and records nothing.
    /// </summary>
    Task SetDataSourceEnabled(int dataSourceId, bool enabled, int? actorUserId, CancellationToken cancellationToken);

    Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken);

    Task<BaseResponse> TestConnection(string connectionString, Data.Enums.DatabaseEngineType databaseEngineType, CancellationToken cancellationToken);

    Task<BaseResponse> TestConnectionAsync(DataSourceData dataSourceData, CancellationToken cancellationToken);
}

internal class DataSourceService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IDataSourceProviderFactory providerFactory,
    IMemoryCache cache,
    ISchemaGraphService schemaGraphService,
    ISecurityAuditRecorder securityAudit,
    IDataSourceTransportPolicy transportPolicy,
    ILogger<DataSourceService> logger,
    IBeaconScheduler? scheduler = null) : IDataSourceService
{
    /// <summary>What a caller is told when a change of target is not confirmed (HTTP 409).</summary>
    public const string TargetChangeUnconfirmedMessage =
        "The new connection points at another server, port, database or engine. Confirm the target change to save it: the column values sampled from the old target are removed.";

    public async Task<BaseResponse> CreateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(dataSourceData.ConnectionString))
        {
            throw new InvalidOperationException("A connection string is required.");
        }

        var connectionString = dataSourceData.ConnectionString;
        transportPolicy.EnsureNewConnectionAllowed(dataSourceData.DataSourceType, dataSourceData.DatabaseEngineType, connectionString);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = new DataSource
        {
            Name = dataSourceData.Name,
            DataSourceType = dataSourceData.DataSourceType,
            EncryptedConnectionData = encryptionService.Encrypt(connectionString),
            DatabaseEngineType = dataSourceData.DatabaseEngineType,
            MetadataLoadingEnabled = dataSourceData.MetadataLoadingEnabled ?? true,
            MetadataMaxTables = dataSourceData.MetadataMaxTables ?? 0,
            MetadataMaxColumnsPerTable = dataSourceData.MetadataMaxColumnsPerTable ?? 0,
            MetadataLoadTableNamesOnly = dataSourceData.MetadataLoadTableNamesOnly ?? false,
            MetadataExcludeSchemas = SchemaList(dataSourceData.MetadataExcludeSchemas),
            MetadataIncludeSchemas = SchemaList(dataSourceData.MetadataIncludeSchemas),
            // Admin-only until a project links it: only sources that existed before project access stay open unlinked.
            OpenWhenUnlinked = false
        };

        var projectIds = dataSourceData.ProjectIds
            .Distinct()
            .ToList();
        context.DataSources.Add(dataSource);
        foreach (var projectId in projectIds)
        {
            context.ProjectDataSources.Add(new ProjectDataSource { ProjectId = projectId, DataSource = dataSource, LinkedByUserId = dataSourceData.LinkedByUserId });
        }

        await securityAudit.SaveWithEventsAsync(
            context,
            () =>
            [
                new SecurityAuditEntry(SecurityAuditActions.DataSourceCreated)
                    .On(SecurityAuditResources.DataSource, dataSource.Id)
                    .With(new SecurityAuditDetails()
                        .Add("dataSourceType", dataSource.DataSourceType)
                        .AddCode("databaseEngineType", dataSource.DatabaseEngineType?.ToString())
                        .Add("projectIds", projectIds))
            ],
            cancellationToken);
        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, providerFactory, dataSource.Id, dataSource.DataSourceType, dataSource.DatabaseEngineType);

        // For API data sources, import endpoint metadata from OpenAPI spec
        if (dataSourceData.DataSourceType == DataSourceType.Api)
        {
            try
            {
                var provider = providerFactory.GetProvider(DataSourceType.Api);
                var metadata = await provider.GetMetadataAsync(dataSource, cancellationToken);
                if (metadata.Endpoints?.Count > 0)
                {
                    await StoreApiEndpointsAsMetadata(context, dataSource.Id, metadata.Endpoints, cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to import API metadata for data source {Id}. Can be retried via metadata refresh.", dataSource.Id);
            }
        }

        return new BaseResponse
        {
            Success = true
        };
    }

    private static async Task StoreApiEndpointsAsMetadata(
        BeaconContext context,
        int dataSourceId,
        List<ApiEndpointMetadata> endpoints,
        CancellationToken cancellationToken)
    {
        foreach (var endpoint in endpoints)
        {
            var schemaName = endpoint.Tag ?? "default";
            var tableName = $"{endpoint.Method} {endpoint.Path}";

            var metadata = new DatabaseMetadata
            {
                DataSourceId = dataSourceId,
                SchemaName = schemaName,
                TableName = tableName,
                TableDescription = endpoint.Summary ?? endpoint.Description,
                LastRefreshed = DateTime.UtcNow
            };

            // Add response fields as columns
            foreach (var (field, index) in endpoint.ResponseFields.Select((f, i) => (f, i)))
            {
                metadata.Columns.Add(new ColumnMetadata
                {
                    ColumnName = field.Name,
                    DataType = field.Type,
                    IsNullable = true,
                    OrdinalPosition = index,
                    Description = field.Description
                });
            }

            context.DatabaseMetadata.Add(metadata);
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteDataSource(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // IgnoreQueryFilters so archived queries still materialize on the QueryStep→Query navigation.
        // Query is an ArchivableBaseEntity (global ArchivedTime == null filter); without this, qs.Query
        // is null for any archived query and the in-memory qs.Query.ArchivedTime check below throws NRE —
        // on the exact happy path where every referencing query is already archived.
        var dataSource = await context.DataSources
            .IgnoreQueryFilters()
            .Include(x => x.QuerySteps)
                .ThenInclude(qs => qs.Query)
            .Where(x => x.Id == dataSourceId)
            .SingleAsync(cancellationToken);

        // IgnoreQueryFilters also disables the DataSource's own soft-delete filter, so an
        // already-archived source is returned here rather than throwing. Re-archiving would reset
        // ArchivedTime, so treat an already-archived source as a no-op.
        if (dataSource.ArchivedTime != null)
        {
            return;
        }

        EnsureNotHostManaged(dataSource);

        // Check for non-archived queries using this data source
        var unarchivedQueries = dataSource.QuerySteps
            .Where(qs => qs.Query.ArchivedTime == null)
            .Select(qs => qs.Query)
            .DistinctBy(q => q.Id)
            .ToList();

        if (unarchivedQueries.Count > 0)
        {
            var queryNames = string.Join(", ", unarchivedQueries.Select(q => q.Name));
            throw new BeaconException($"Unable to archive data source. The following queries must be archived first: {queryNames}");
        }

        // Check for non-archived migration jobs using this data source (as source or destination)
        var unarchivedMigrationJobs = await context.MigrationJobs
            .Where(mj => (mj.DataSourceId == dataSourceId || mj.DestinationDataSourceId == dataSourceId)
                         && mj.ArchivedTime == null)
            .ToListAsync(cancellationToken);

        if (unarchivedMigrationJobs.Count > 0)
        {
            var migrationJobNames = string.Join(", ", unarchivedMigrationJobs.Select(mj => mj.Name));
            throw new BeaconException($"Unable to archive data source. The following migration jobs must be archived first: {migrationJobNames}");
        }

        // The column values sampled from the source go with it, archived tables' columns included: an archived source is
        // never read again, so nothing keeps a copy of its rows.
        await DatabaseMetadataService.ClearStoredSampleValuesAsync(context, dataSourceId, cancellationToken);

        dataSource.Archive();
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.DataSourceDeleted).On(SecurityAuditResources.DataSource, dataSourceId),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        EvictMetadata(dataSourceId);
    }

    public async Task UpdateDataSource(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceData.DataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {dataSourceData.DataSourceId} not found.");

        EnsureNotHostManaged(dataSource);

        if (dataSource.DataSourceType == DataSourceType.Database && dataSourceData.DatabaseEngineType == null)
        {
            throw new InvalidOperationException("A database data source needs a database engine.");
        }

        // No connection given keeps the stored one, which is never shown and so never sent back; a stored one Beacon cannot
        // read (encrypted under a key no longer configured) cannot be kept.
        var storedConnection = StoredConnection(dataSource.EncryptedConnectionData, dataSource.Id, dataSource.DatabaseEngineType);
        var keepsConnection = string.IsNullOrWhiteSpace(dataSourceData.ConnectionString);
        if (keepsConnection && storedConnection == null)
        {
            throw new InvalidOperationException("The stored connection cannot be read, so it cannot be kept. Enter the connection again.");
        }

        var engineChanged = dataSource.DatabaseEngineType != dataSourceData.DatabaseEngineType;
        if (keepsConnection && engineChanged)
        {
            throw new InvalidOperationException("Changing the engine needs the connection entered again: a stored connection is never read as another engine's.");
        }

        var connectionString = keepsConnection ? storedConnection! : dataSourceData.ConnectionString!;
        var connectionChanged = storedConnection == null || !string.Equals(storedConnection, connectionString, StringComparison.Ordinal);
        if (connectionChanged || engineChanged)
        {
            // A connection kept as it is stays a stored one (warned about when it connects); a new one must authenticate
            // the server.
            transportPolicy.EnsureNewConnectionAllowed(dataSource.DataSourceType, dataSourceData.DatabaseEngineType, connectionString);
        }

        // Pointing a source somewhere else is confirmed: the queries, contracts and grounding built on the old target would
        // read the new one.
        var targetChanged = engineChanged
            || (connectionChanged && TargetChanged(dataSource, storedConnection, dataSourceData.DatabaseEngineType, connectionString));
        if (targetChanged && !dataSourceData.ConfirmTargetChange)
        {
            throw new BeaconConflictException(TargetChangeUnconfirmedMessage);
        }

        // A metadata option left out (null) keeps the stored value.
        var metadataLoadingEnabled = dataSourceData.MetadataLoadingEnabled ?? dataSource.MetadataLoadingEnabled;
        var metadataMaxTables = dataSourceData.MetadataMaxTables ?? dataSource.MetadataMaxTables;
        var metadataMaxColumnsPerTable = dataSourceData.MetadataMaxColumnsPerTable ?? dataSource.MetadataMaxColumnsPerTable;
        var metadataLoadTableNamesOnly = dataSourceData.MetadataLoadTableNamesOnly ?? dataSource.MetadataLoadTableNamesOnly;
        var excludeSchemas = dataSourceData.MetadataExcludeSchemas == null ? dataSource.MetadataExcludeSchemas : SchemaList(dataSourceData.MetadataExcludeSchemas);
        var includeSchemas = dataSourceData.MetadataIncludeSchemas == null ? dataSource.MetadataIncludeSchemas : SchemaList(dataSourceData.MetadataIncludeSchemas);
        var changes = new (string Field, bool Changed)[]
        {
            (nameof(DataSource.Name), dataSource.Name != dataSourceData.Name),
            (nameof(DataSource.DatabaseEngineType), engineChanged),
            (nameof(DataSource.MetadataLoadingEnabled), dataSource.MetadataLoadingEnabled != metadataLoadingEnabled),
            (nameof(DataSource.MetadataMaxTables), dataSource.MetadataMaxTables != metadataMaxTables),
            (nameof(DataSource.MetadataMaxColumnsPerTable), dataSource.MetadataMaxColumnsPerTable != metadataMaxColumnsPerTable),
            (nameof(DataSource.MetadataLoadTableNamesOnly), dataSource.MetadataLoadTableNamesOnly != metadataLoadTableNamesOnly),
            (nameof(DataSource.MetadataExcludeSchemas), dataSource.MetadataExcludeSchemas != excludeSchemas),
            (nameof(DataSource.MetadataIncludeSchemas), dataSource.MetadataIncludeSchemas != includeSchemas)
        };
        var changedFields = changes
            .Where(x => x.Changed)
            .Select(x => x.Field)
            .ToList();

        var previousEngine = dataSource.DatabaseEngineType;
        dataSource.Name = dataSourceData.Name;
        dataSource.EncryptedConnectionData = encryptionService.Encrypt(connectionString);
        dataSource.DatabaseEngineType = dataSourceData.DatabaseEngineType;
        dataSource.MetadataLoadingEnabled = metadataLoadingEnabled;
        dataSource.MetadataMaxTables = metadataMaxTables;
        dataSource.MetadataMaxColumnsPerTable = metadataMaxColumnsPerTable;
        dataSource.MetadataLoadTableNamesOnly = metadataLoadTableNamesOnly;
        dataSource.MetadataExcludeSchemas = excludeSchemas;
        dataSource.MetadataIncludeSchemas = includeSchemas;

        // The column values sampled from the old target are not the new one's: they go, archived tables' columns included,
        // in the same unit of work as the change. The stored tables and columns stay until the re-scan enqueued below
        // replaces them (their curated descriptions are kept by name).
        if (targetChanged)
        {
            await DatabaseMetadataService.ClearStoredSampleValuesAsync(context, dataSource.Id, cancellationToken);
        }

        // A new connection (its credentials, host or database) is a credential change; any other edit is an update. The
        // event names the fields that changed and whether the target did, never a value.
        if (connectionChanged || changedFields.Count > 0)
        {
            await securityAudit.StageAsync(
                context,
                new SecurityAuditEntry(connectionChanged ? SecurityAuditActions.CredentialsRotated : SecurityAuditActions.DataSourceUpdated)
                    .On(SecurityAuditResources.DataSource, dataSource.Id)
                    .With(new SecurityAuditDetails()
                        .Add("connectionChanged", connectionChanged)
                        .Add("targetChanged", targetChanged)
                        .AddCodes("changedFields", changedFields)),
                cancellationToken);
        }

        await context.SaveChangesAsync(cancellationToken);
        DataSourceReadOnlyEnforcement.WarnIfNotEnforced(logger, providerFactory, dataSource.Id, dataSource.DataSourceType, dataSource.DatabaseEngineType);

        // The connections pooled with the old credentials are not reused.
        if (connectionChanged || engineChanged)
        {
            ClearConnectionPool(dataSource.Id, dataSource.DataSourceType, previousEngine, storedConnection);
        }

        // The connection or the metadata options may have changed: the cached metadata described the old ones.
        EvictMetadata(dataSource.Id);

        if (targetChanged)
        {
            await EnqueueMetadataRefreshAsync(dataSource);
        }
    }

    public async Task SetDataSourceEnabled(int dataSourceId, bool enabled, int? actorUserId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dataSource = await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {dataSourceId} not found.");

        EnsureNotHostManaged(dataSource);

        if ((dataSource.DisabledTime == null) == enabled)
        {
            return;
        }

        dataSource.DisabledTime = enabled ? null : DateTime.UtcNow;
        dataSource.DisabledByUserId = enabled ? null : actorUserId;
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(enabled ? SecurityAuditActions.DataSourceEnabled : SecurityAuditActions.DataSourceDisabled)
                .On(SecurityAuditResources.DataSource, dataSourceId),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogWarning("Data source {DataSourceId} was {State} by user {ActorUserId}", dataSourceId, enabled ? "enabled" : "disabled", actorUserId);

        // A disabled source's catalog is not served (the metadata service answers an empty one) and an enabled one's is read
        // again: neither is served from the cache built before the change.
        EvictMetadata(dataSourceId);

        if (!enabled)
        {
            // No idle connection opened before the source was disabled is reused.
            ClearConnectionPool(
                dataSourceId,
                dataSource.DataSourceType,
                dataSource.DatabaseEngineType,
                StoredConnection(dataSource.EncryptedConnectionData, dataSource.Id, dataSource.DatabaseEngineType));
        }
    }

    public async Task<BaseResponse> TestConnection(string connectionString, Data.Enums.DatabaseEngineType databaseEngineType, CancellationToken cancellationToken)
    {
        var refusal = TransportRefusal(DataSourceType.Database, databaseEngineType, connectionString);
        if (refusal != null)
        {
            return refusal;
        }

        try
        {
            await using var connection = DbConnectionFactory.CreateConnection(databaseEngineType, connectionString);
            await connection.OpenAsync(cancellationToken);
            await connection.CloseAsync();

            return new BaseResponse
            {
                Success = true,
                Message = "Connection successful"
            };
        }
        catch (Exception ex)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection failed: {ConnectionFailureDescriber.Describe(ex)}"
            };
        }
    }

    public async Task<BaseResponse> TestConnectionAsync(DataSourceData dataSourceData, CancellationToken cancellationToken)
    {
        var connectionString = dataSourceData.ConnectionString ?? string.Empty;
        var refusal = TransportRefusal(dataSourceData.DataSourceType, dataSourceData.DatabaseEngineType, connectionString);
        if (refusal != null)
        {
            return refusal;
        }

        try
        {
            // For database types, use the connection factory directly
            if (dataSourceData.DataSourceType == DataSourceType.Database && dataSourceData.DatabaseEngineType.HasValue)
            {
                return await TestConnection(connectionString, dataSourceData.DatabaseEngineType.Value, cancellationToken);
            }

            // For non-database types (CloudWatch, Databricks, BigQuery), use the provider pattern
            var provider = providerFactory.GetProvider(dataSourceData.DataSourceType);
            var tempDataSource = new DataSource
            {
                Name = dataSourceData.Name ?? "connection-test",
                DataSourceType = dataSourceData.DataSourceType,
                DatabaseEngineType = dataSourceData.DatabaseEngineType,
                EncryptedConnectionData = encryptionService.Encrypt(connectionString)
            };

            var result = await provider.TestConnectionAsync(tempDataSource, cancellationToken);

            return new BaseResponse
            {
                Success = result.Success,
                Message = result.Success ? "Connection successful" : $"Connection failed: {result.ErrorMessage}"
            };
        }
        catch (Exception ex)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection failed: {ConnectionFailureDescriber.Describe(ex)}"
            };
        }
    }

    // A connection test is refused, never opened, when the connection does not authenticate the server: its message names
    // the option to change, never a value from the connection string.
    private BaseResponse? TransportRefusal(DataSourceType dataSourceType, DatabaseEngineType? databaseEngineType, string connectionString)
    {
        try
        {
            transportPolicy.EnsureNewConnectionAllowed(dataSourceType, databaseEngineType, connectionString);

            return null;
        }
        catch (DataSourceTransportException ex)
        {
            return new BaseResponse
            {
                Success = false,
                Message = $"Connection refused: {ex.Message}"
            };
        }
    }

    // Cached metadata and the relationship graph are rebuilt on their next read; an archived source's reads are refused.
    // A changed source may be refreshed at once: its forced-refresh cooldown starts over.
    private void EvictMetadata(int dataSourceId)
    {
        cache.Remove(DatabaseMetadataService.GetCacheKey(dataSourceId));
        cache.Remove(ForcedMetadataRefreshCooldown.CacheKey(dataSourceId));
        schemaGraphService.Invalidate(dataSourceId);
    }

    // A source pointed at another target still has the old target's tables and columns stored: a metadata re-scan replaces
    // them in the background, when the source loads metadata and Beacon may read it. Until it finishes, the old target's
    // schema is shown. The change is saved already, so a failure to enqueue is logged by its type only.
    private async Task EnqueueMetadataRefreshAsync(DataSource dataSource)
    {
        if (dataSource.DataSourceType != DataSourceType.Database
            || dataSource.DatabaseEngineType == null
            || !dataSource.MetadataLoadingEnabled
            || dataSource.AllowMigrationWrites
            || dataSource.DisabledTime != null)
        {
            return;
        }

        try
        {
            var jobId = scheduler == null ? null : await scheduler.EnqueueMetadataRefresh(dataSource.Id);
            if (jobId == null)
            {
                logger.LogWarning(
                    "No metadata refresh was enqueued for data source {DataSourceId} after its target changed: this host runs no such job, so the old target's schema stays until its metadata is refreshed",
                    dataSource.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The metadata refresh of data source {DataSourceId} could not be enqueued after its target changed ({ExceptionType}); the old target's schema stays until its metadata is refreshed",
                dataSource.Id,
                ex.GetType().Name);
        }
    }

    // Host-managed data sources belong to the host application (ExposeDbContext): their connection, engine and
    // lifetime are declared in code and re-applied at every startup, so the UI/REST surface must not change them.
    private static void EnsureNotHostManaged(DataSource dataSource)
    {
        if (dataSource.HostManagedKey != null)
        {
            throw new InvalidOperationException(
                $"Data source '{dataSource.Name}' is managed by the host application (ExposeDbContext) and cannot be edited or deleted here.");
        }
    }

    // The stored connection data in plain text, only to compare an update's target with it and to empty its driver pool;
    // null when Beacon cannot read it (encrypted under a key no longer configured), logged by the source's id and engine
    // and the exception's type only, never its message.
    private string? StoredConnection(string? storedEncrypted, int dataSourceId, DatabaseEngineType? engine)
    {
        if (string.IsNullOrEmpty(storedEncrypted))
        {
            return null;
        }

        try
        {
            return encryptionService.Decrypt(storedEncrypted);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The stored connection of data source {DataSourceId} ({Engine}) could not be decrypted ({ExceptionType}); it cannot be kept or have its connection pool cleared",
                dataSourceId,
                engine,
                ex.GetType().Name);

            return null;
        }
    }

    private static string? SchemaList(List<string>? schemas)
    {
        return schemas is { Count: > 0 } ? string.Join(",", schemas) : null;
    }

    // Whether the new connection goes to another host, port or database than the stored one, as the source's transport
    // inspector reads them. A target that cannot be read on either side counts as changed: the change must be confirmed.
    private bool TargetChanged(DataSource dataSource, string? storedConnection, DatabaseEngineType? engine, string connectionString)
    {
        if (storedConnection == null)
        {
            return true;
        }

        var before = transportPolicy.Inspect(dataSource.DataSourceType, dataSource.DatabaseEngineType, storedConnection).Target;
        var after = transportPolicy.Inspect(dataSource.DataSourceType, engine, connectionString).Target;
        if (before == null || after == null)
        {
            return true;
        }

        return !string.Equals(before.Host, after.Host, StringComparison.OrdinalIgnoreCase)
            || before.Port != after.Port
            || !string.Equals(before.Database, after.Database, StringComparison.Ordinal);
    }

    // Empties the driver's pools of the old connection string (PostgreSQL, MySQL with its read-only pool, SQL Server,
    // Synapse; Snowflake and the JSON-configured connectors keep no pool Beacon can empty, which is logged for a database
    // engine). The change is saved already, so a failure is logged by its type only, never its message, which can quote
    // the connection string.
    private void ClearConnectionPool(int dataSourceId, DataSourceType dataSourceType, DatabaseEngineType? engine, string? connectionString)
    {
        if (dataSourceType != DataSourceType.Database || engine == null || connectionString == null)
        {
            return;
        }

        try
        {
            if (!DbConnectionFactory.ClearPool(engine.Value, connectionString))
            {
                logger.LogWarning(
                    "The connection pool of data source {DataSourceId} was not cleared: no pool clearer is registered for {Engine}",
                    dataSourceId,
                    engine.Value);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "The connection pool of data source {DataSourceId} could not be cleared ({ExceptionType}); its pooled connections close when they expire",
                dataSourceId,
                ex.GetType().Name);
        }
    }
}
