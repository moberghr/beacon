using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.Services;

public class DatabaseMetadataService(
    IDbContextFactory<BeaconContext> contextFactory,
    IDataSourceConnectionResolver connectionResolver,
    IEnumerable<IDatabaseMetadataExtractor> metadataExtractors,
    IColumnValueSampler columnValueSampler,
    ISharedSourceSettings sharedSourceSettings,
    IMemoryCache cache,
    ISchemaGraphService schemaGraphService,
    ILogger<DatabaseMetadataService> logger,
    ICatalogSnapshotCache catalogSnapshots)
    : IDatabaseMetadataService
{
    /// <summary>A service whose refreshes leave the ask path's catalog snapshots to expire on their own.</summary>
    [Obsolete("Pass the ICatalogSnapshotCache so a refresh invalidates the catalog snapshots the ask path reads.")]
    public DatabaseMetadataService(
        IDbContextFactory<BeaconContext> contextFactory,
        IDataSourceConnectionResolver connectionResolver,
        IEnumerable<IDatabaseMetadataExtractor> metadataExtractors,
        IColumnValueSampler columnValueSampler,
        ISharedSourceSettings sharedSourceSettings,
        IMemoryCache cache,
        ISchemaGraphService schemaGraphService,
        ILogger<DatabaseMetadataService> logger)
        : this(contextFactory, connectionResolver, metadataExtractors, columnValueSampler, sharedSourceSettings, cache, schemaGraphService, logger, NoCatalogSnapshotCache.Instance)
    {
    }

    private static readonly string CacheKeyPrefix = BeaconCacheEntries.Key("db-metadata:");
    private const int MaxLoggedSkippedNames = 50;
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(1);

    // Lock per data source to prevent concurrent refresh operations; an entry lives only while someone holds or waits for it
    private static readonly KeyedRefreshLocks _refreshLocks = new();

    public async Task<DatabaseMetadataSnapshot> RefreshMetadataAsync(int dataSourceId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        // Try to acquire the lock for this specific data source - if already locked, wait
        var refreshLock = await _refreshLocks.AcquireAsync(dataSourceId, cancellationToken);
        try
        {
            // Double-check cache after acquiring lock (another thread may have just completed refresh).
            // Skipped on explicit user-driven refresh so a hot cache doesn't mask a live re-scan.
            // The cached snapshot is served only while the source may still be read; otherwise the full path below refuses it.
            if (!forceRefresh && cache.TryGetValue(GetCacheKey(dataSourceId), out DatabaseMetadataSnapshot? cachedSnapshot) && cachedSnapshot != null)
            {
                var cachedState = await ReadStateAsync(dataSourceId, cancellationToken);
                if (cachedState is { Readable: true })
                {
                    logger.LogDebug("Metadata for data source {DataSourceId} was refreshed by another thread", dataSourceId);
                    return cachedSnapshot;
                }

                cache.Remove(GetCacheKey(dataSourceId));
            }

            logger.LogInformation("Refreshing metadata for data source {DataSourceId}", dataSourceId);

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            // An archived data source is not found (the soft-delete filter), so it is never scanned.
            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken);
            if (dataSource == null)
                throw new BeaconNotFoundException($"Data source {dataSourceId} not found");

            // Only database type data sources have metadata
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new BeaconException($"Data source {dataSourceId} is not a database type");

            // Host-managed sources describe only the allow-listed slice of the host's EF model. A live catalog scan
            // (and value sampling) would expose every table the login can see, so it is never run for them.
            if (dataSource.HostManagedKey != null)
            {
                throw new InvalidOperationException(
                    "Metadata for a host data source comes from the host's EF model and is refreshed when the host starts (SyncBeaconHostDataSourcesAsync).");
            }

            // A migration write target is never read: its login may write, and only the migration writer connects with it.
            // The resolver refuses it too, and applies the transport check.
            MigrationWriteTargets.EnsureReadable(dataSource);

            // What the scan reads with, kept to tell whether the source changed while it ran.
            var scannedConnection = dataSource.EncryptedConnectionData;
            var scannedEngine = dataSource.DatabaseEngineType;

            // Extract metadata based on database type using registered extractors
            var connectionString = connectionResolver.GetConnectionString(dataSource);
            var extractor = metadataExtractors.FirstOrDefault(e => e.SupportedEngineType == dataSource.DatabaseEngineType.Value)
                ?? throw new NotSupportedException($"No metadata extractor registered for database type {dataSource.DatabaseEngineType}. Make sure to register the appropriate connector.");
            var tables = await extractor.ExtractMetadataAsync(connectionString, cancellationToken);

            // Apply per-datasource filters
            tables = ApplyMetadataFilters(tables, dataSource);
            (tables, var skippedNames) = SkipUnsafeNames(tables, dataSourceId);

            // Enrich with representative sample values for LLM grounding (failure-tolerant): only while every live project
            // that links the source allows it, screened with every such project's custom PII patterns.
            var mcpSettings = await sharedSourceSettings.GetDataSourceSettingsAsync(dataSourceId, cancellationToken);
            if (mcpSettings.EnableSampleValueCollection)
            {
                tables = await columnValueSampler.EnrichWithSampleValuesAsync(
                    dataSource,
                    tables,
                    mcpSettings.CustomPiiPatterns,
                    cancellationToken);
            }

            // Store in database. What was built from the stored catalog is dropped at once, before anything else can fail:
            // the relationship graph is rebuilt on its next read, the ask path's catalog snapshots are loaded again.
            var stored = await StoreMetadataAsync(dataSourceId, tables, cancellationToken);
            schemaGraphService.Invalidate(dataSourceId);
            catalogSnapshots.Invalidate(dataSourceId);

            // Update cache. A scan that a disable, a migration-write mark or a connection change overtook publishes nothing to the cache:
            // the source is read again here, after the scan, and a disabled or write-target source returns no catalog.
            var snapshot = new DatabaseMetadataSnapshot(dataSourceId, scannedEngine, stored, DateTime.UtcNow)
            {
                SkippedNameCount = skippedNames
            };
            var current = await ReadStateAsync(dataSourceId, cancellationToken);
            var unchanged = current is { Readable: true }
                && current.EncryptedConnectionData == scannedConnection
                && current.DatabaseEngineType == scannedEngine;
            if (unchanged)
            {
                cache.Set(GetCacheKey(dataSourceId), snapshot, BeaconCacheEntries.For(CacheExpiration));
            }

            if (current is not { Readable: true })
            {
                logger.LogInformation("Metadata refresh of data source {DataSourceId} finished after the source was disabled or marked as a migration write target; nothing is served", dataSourceId);
                return EmptySnapshot(dataSourceId, scannedEngine);
            }

            if (!unchanged)
            {
                logger.LogInformation("Metadata refresh of data source {DataSourceId} finished after its connection changed; its snapshot is not cached", dataSourceId);
            }

            logger.LogInformation("Refreshed metadata for data source {DataSourceId}: {TableCount} tables", dataSourceId, tables.Count);

            return snapshot;
        }
        finally
        {
            refreshLock.Dispose();
        }
    }

    public async Task<DatabaseMetadataSnapshot> GetMetadataAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        // A migration write target has no catalog in Beacon (it is never read, so neither its stored tables nor a scan are
        // offered to anyone), nor has a disabled source while it is disabled (its stored tables and sampled values are kept
        // for when it is enabled, but not served). Its current state is read first — before the cache and before any
        // source-specific loader — so a snapshot cached before the change, by this instance or another, is never served.
        // An archived data source is not found (the soft-delete filter).
        var state = await ReadStateAsync(dataSourceId, cancellationToken)
            ?? throw new BeaconNotFoundException($"Data source {dataSourceId} not found");
        if (!state.Readable)
        {
            logger.LogDebug("Metadata is not served for disabled or write-target data source {DataSourceId}", dataSourceId);
            cache.Remove(GetCacheKey(dataSourceId));
            return EmptySnapshot(dataSourceId, state.DatabaseEngineType);
        }

        // Try to get from cache first
        if (cache.TryGetValue(GetCacheKey(dataSourceId), out DatabaseMetadataSnapshot? cachedSnapshot) && cachedSnapshot != null)
        {
            return cachedSnapshot;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get the data source to know the database type and metadata loading options. An archived data source is not
        // found (the soft-delete filter); archiving evicts its cached snapshot.
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken);
        if (dataSource == null)
            throw new BeaconNotFoundException($"Data source {dataSourceId} not found");

        // For API data sources, load stored endpoint metadata directly
        if (dataSource.DataSourceType == Data.Enums.DataSourceType.Api)
        {
            return await LoadApiMetadataAsync(context, dataSourceId, cancellationToken);
        }

        if (!dataSource.DatabaseEngineType.HasValue)
            throw new BeaconException($"Data source {dataSourceId} is not a database type");

        // Check if metadata loading is disabled for this data source
        if (!dataSource.MetadataLoadingEnabled)
        {
            logger.LogDebug("Metadata loading is disabled for data source {DataSourceId}", dataSourceId);
            return new DatabaseMetadataSnapshot(dataSourceId, dataSource.DatabaseEngineType.Value, new List<TableMetadataDto>(), DateTime.UtcNow);
        }

        // Parse per-datasource schema filters
        var includeSchemas = ParseSchemaList(dataSource.MetadataIncludeSchemas);
        var excludeSchemas = ParseSchemaList(dataSource.MetadataExcludeSchemas);

        // Try to get from database
        var metadataQuery = context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId);

        // Apply schema filters
        if (includeSchemas.Count > 0)
        {
            var lower = includeSchemas.Select(s => s.ToLowerInvariant()).ToList();
            metadataQuery = metadataQuery.Where(m => lower.Contains(m.SchemaName.ToLower()));
        }
        else if (excludeSchemas.Count > 0)
        {
            var lower = excludeSchemas.Select(s => s.ToLowerInvariant()).ToList();
            metadataQuery = metadataQuery.Where(m => !lower.Contains(m.SchemaName.ToLower()));
        }

        // Apply table limit
        if (dataSource.MetadataMaxTables > 0)
        {
            // Ordered so the separate column and index queries of the split load agree on which tables the limit kept.
            metadataQuery = metadataQuery
                .OrderBy(m => m.Id)
                .Take(dataSource.MetadataMaxTables);
            logger.LogDebug("Limiting metadata to {MaxTables} tables for data source {DataSourceId}", dataSource.MetadataMaxTables, dataSourceId);
        }

        // Load metadata based on LoadTableNamesOnly setting
        List<DatabaseMetadata> metadata;
        if (dataSource.MetadataLoadTableNamesOnly)
        {
            metadata = await metadataQuery.ToListAsync(cancellationToken);
            logger.LogDebug("Loaded {Count} table names only (columns excluded) for data source {DataSourceId}", metadata.Count, dataSourceId);
        }
        else
        {
            // Read-only and split: one joined query over two collections would repeat every column once per index.
            metadata = await metadataQuery
                .AsNoTracking()
                .Include(m => m.Columns)
                .Include(m => m.Indexes)
                .AsSplitQuery()
                .ToListAsync(cancellationToken);
        }

        if (metadata.Any())
        {
            var maxColumnsPerTable = dataSource.MetadataMaxColumnsPerTable;
            var loadTableNamesOnly = dataSource.MetadataLoadTableNamesOnly;

            var tables = metadata.Select(m => new TableMetadataDto(
                m.SchemaName,
                m.TableName,
                loadTableNamesOnly ? new List<ColumnMetadataDto>() : m.Columns
                    .OrderBy(c => c.OrdinalPosition)
                    .Take(maxColumnsPerTable > 0 ? maxColumnsPerTable : int.MaxValue)
                    .Select(c => new ColumnMetadataDto(
                        c.ColumnName,
                        c.DataType,
                        c.IsNullable,
                        c.IsPrimaryKey,
                        c.IsForeignKey,
                        c.OrdinalPosition,
                        c.ForeignKeyTable,
                        c.ForeignKeyColumn,
                        c.DefaultValue,
                        c.MaxLength,
                        c.Description,
                        DeserializeSampleValues(c.SampleValues),
                        c.ForeignKeySchema,
                        c.ForeignKeyConstraintName,
                        c.SampleValuesComplete
                    )
                    {
                        ForeignKeys = ColumnForeignKeys.Deserialize(c.ForeignKeys)
                    }).ToList(),
                loadTableNamesOnly ? new List<IndexMetadataDto>() : m.Indexes.Select(i => new IndexMetadataDto(
                    i.IndexName,
                    i.IsUnique,
                    i.IsPrimaryKey,
                    i.Columns
                )).ToList(),
                m.TableDescription
            )).ToList();

            var snapshot = new DatabaseMetadataSnapshot(dataSourceId, dataSource.DatabaseEngineType.Value, tables, metadata.Max(m => m.LastRefreshed));
            cache.Set(GetCacheKey(dataSourceId), snapshot, BeaconCacheEntries.For(CacheExpiration));

            return snapshot;
        }

        // A host data source that exposes nothing yet has nothing to scan — never fall through to a live refresh.
        if (dataSource.HostManagedKey != null)
        {
            return new DatabaseMetadataSnapshot(dataSourceId, dataSource.DatabaseEngineType.Value, new List<TableMetadataDto>(), DateTime.UtcNow);
        }

        // If no data in database, refresh from source
        return await RefreshMetadataAsync(dataSourceId, forceRefresh: false, cancellationToken);
    }

    // Replaces the data source's stored metadata with the extracted tables, keeping the curated descriptions (those a person
    // set by applying a documentation patch) over the extracted ones. Returns the tables as stored.
    private async Task<IReadOnlyList<TableMetadataDto>> StoreMetadataAsync(int dataSourceId, IReadOnlyList<TableMetadataDto> tables, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Remove existing metadata for this data source
        var existingMetadata = await context.DatabaseMetadata
            .Include(m => m.Columns)
            .Where(m => m.DataSourceId == dataSourceId)
            .ToListAsync(cancellationToken);

        var curated = await CuratedDescriptions.LoadAsync(context, dataSourceId, existingMetadata, cancellationToken);
        var merged = curated.ApplyTo(tables);

        context.DatabaseMetadata.RemoveRange(existingMetadata);

        // Save changes to commit the deletions before adding new records
        // This prevents unique constraint violations on (DataSourceId, SchemaName, TableName)
        await context.SaveChangesAsync(cancellationToken);

        // Add new metadata
        foreach (var table in merged)
        {
            var metadata = new DatabaseMetadata
            {
                DataSourceId = dataSourceId,
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                TableDescription = table.Description,
                LastRefreshed = DateTime.UtcNow
            };

            foreach (var column in table.Columns)
            {
                metadata.Columns.Add(new ColumnMetadata
                {
                    ColumnName = column.ColumnName,
                    DataType = column.DataType,
                    IsNullable = column.IsNullable,
                    IsPrimaryKey = column.IsPrimaryKey,
                    IsForeignKey = column.IsForeignKey,
                    OrdinalPosition = column.OrdinalPosition,
                    ForeignKeyTable = column.ForeignKeyTable,
                    ForeignKeyColumn = column.ForeignKeyColumn,
                    ForeignKeySchema = column.ForeignKeySchema,
                    ForeignKeyConstraintName = column.ForeignKeyConstraintName,
                    ForeignKeys = ColumnForeignKeys.Serialize(column.ForeignKeys),
                    DefaultValue = column.DefaultValue,
                    MaxLength = column.MaxLength,
                    Description = column.Description,
                    SampleValues = SerializeSampleValues(column.SampleValues),
                    SampleValuesComplete = column.SampleValuesComplete
                });
            }

            foreach (var index in table.Indexes)
            {
                metadata.Indexes.Add(new IndexMetadata
                {
                    IndexName = index.IndexName,
                    IsUnique = index.IsUnique,
                    IsPrimaryKey = index.IsPrimaryKey,
                    Columns = index.Columns
                });
            }

            context.DatabaseMetadata.Add(metadata);
        }

        await context.SaveChangesAsync(cancellationToken);

        if (curated.Count > 0)
        {
            logger.LogInformation("Kept {CuratedCount} curated descriptions for data source {DataSourceId} on refresh", curated.Count, dataSourceId);
        }

        return merged;
    }

    /// <summary>
    /// Leaves out every table, every column and every foreign-key target whose name carries a quote, bracket, semicolon or
    /// control character (<see cref="SqlIdentifierGuard.HasUnsafeCharacters"/>). Beacon builds SQL from catalog names (documentation enum
    /// discovery, value probes, sampling), and such a name could end its quoting in some dialect, so it is never stored —
    /// nor its description, a curated one included. The names left out are logged (identifiers only, made safe for the log
    /// by <see cref="SqlIdentifierGuard.ForLog"/>, at most <see cref="MaxLoggedSkippedNames"/> of them) and counted.
    /// </summary>
    private (IReadOnlyList<TableMetadataDto> Kept, int SkippedNames) SkipUnsafeNames(IReadOnlyList<TableMetadataDto> tables, int dataSourceId)
    {
        var kept = new List<TableMetadataDto>(tables.Count);
        var skippedTables = new List<string>();
        var skippedColumns = new List<string>();
        var skippedForeignKeyTargets = new List<string>();
        foreach (var table in tables)
        {
            if (SqlIdentifierGuard.HasUnsafeCharacters(table.SchemaName) || SqlIdentifierGuard.HasUnsafeCharacters(table.TableName))
            {
                skippedTables.Add(SqlIdentifierGuard.ForLog($"{table.SchemaName}.{table.TableName}"));
                continue;
            }

            var safeColumns = table.Columns
                .Where(x => !SqlIdentifierGuard.HasUnsafeCharacters(x.ColumnName))
                .ToList();
            skippedColumns.AddRange(table.Columns
                .Where(x => SqlIdentifierGuard.HasUnsafeCharacters(x.ColumnName))
                .Select(x => SqlIdentifierGuard.ForLog($"{table.SchemaName}.{table.TableName}.{x.ColumnName}")));

            var columns = new List<ColumnMetadataDto>(safeColumns.Count);
            var columnsChanged = safeColumns.Count != table.Columns.Count;
            foreach (var column in safeColumns)
            {
                var safeColumn = WithoutUnsafeForeignKeyTargets(column, out var skippedTargets);
                skippedForeignKeyTargets.AddRange(skippedTargets.Select(x => SqlIdentifierGuard.ForLog($"{table.SchemaName}.{table.TableName}.{column.ColumnName} -> {x}")));
                columnsChanged |= !ReferenceEquals(safeColumn, column);
                columns.Add(safeColumn);
            }

            kept.Add(columnsChanged ? table with { Columns = columns } : table);
        }

        if (skippedTables.Count > 0 || skippedColumns.Count > 0 || skippedForeignKeyTargets.Count > 0)
        {
            logger.LogWarning(
                "Metadata refresh of data source {DataSourceId} left out {TableCount} tables, {ColumnCount} columns and {ForeignKeyTargetCount} foreign-key targets whose names carry quote, bracket, semicolon or control characters: {SkippedNames}",
                dataSourceId,
                skippedTables.Count,
                skippedColumns.Count,
                skippedForeignKeyTargets.Count,
                skippedTables
                    .Concat(skippedColumns)
                    .Concat(skippedForeignKeyTargets)
                    .Take(MaxLoggedSkippedNames)
                    .ToList());
        }

        return (kept, skippedTables.Count + skippedColumns.Count + skippedForeignKeyTargets.Count);
    }

    /// <summary>
    /// Applies per-datasource filters to metadata to control memory usage.
    /// </summary>
    private IReadOnlyList<TableMetadataDto> ApplyMetadataFilters(IReadOnlyList<TableMetadataDto> tables, DataSource dataSource)
    {
        var filteredTables = tables.AsEnumerable();

        var includeSchemas = ParseSchemaList(dataSource.MetadataIncludeSchemas);
        var excludeSchemas = ParseSchemaList(dataSource.MetadataExcludeSchemas);

        // Apply schema filters
        if (includeSchemas.Count > 0)
        {
            var lower = includeSchemas.Select(s => s.ToLowerInvariant()).ToHashSet();
            filteredTables = filteredTables.Where(t => lower.Contains(t.SchemaName.ToLowerInvariant()));
            logger.LogDebug("Filtering metadata to include only schemas: {Schemas}", string.Join(", ", includeSchemas));
        }
        else if (excludeSchemas.Count > 0)
        {
            var lower = excludeSchemas.Select(s => s.ToLowerInvariant()).ToHashSet();
            filteredTables = filteredTables.Where(t => !lower.Contains(t.SchemaName.ToLowerInvariant()));
            logger.LogDebug("Filtering metadata to exclude schemas: {Schemas}", string.Join(", ", excludeSchemas));
        }

        // Apply table limit
        if (dataSource.MetadataMaxTables > 0)
        {
            filteredTables = filteredTables.Take(dataSource.MetadataMaxTables);
            logger.LogDebug("Limiting metadata to {MaxTables} tables", dataSource.MetadataMaxTables);
        }

        var result = filteredTables.ToList();

        // Apply column limit or load table names only
        if (dataSource.MetadataLoadTableNamesOnly)
        {
            logger.LogDebug("Loading table names only (columns and indexes excluded)");
            return result.Select(t => new TableMetadataDto(
                t.SchemaName,
                t.TableName,
                new List<ColumnMetadataDto>(),
                new List<IndexMetadataDto>(),
                t.Description
            )).ToList();
        }

        if (dataSource.MetadataMaxColumnsPerTable > 0)
        {
            logger.LogDebug("Limiting columns to {MaxColumns} per table", dataSource.MetadataMaxColumnsPerTable);
            return result.Select(t => new TableMetadataDto(
                t.SchemaName,
                t.TableName,
                t.Columns.Take(dataSource.MetadataMaxColumnsPerTable).ToList(),
                t.Indexes,
                t.Description
            )).ToList();
        }

        return result;
    }

    private async Task<DatabaseMetadataSnapshot> LoadApiMetadataAsync(
        BeaconContext context,
        int dataSourceId,
        CancellationToken cancellationToken)
    {
        var metadata = await context.DatabaseMetadata
            .AsNoTracking()
            .Where(m => m.DataSourceId == dataSourceId && m.ArchivedTime == null)
            .Include(m => m.Columns)
            .ToListAsync(cancellationToken);

        var tables = metadata.Select(m => new TableMetadataDto(
            m.SchemaName,
            m.TableName,
            m.Columns.OrderBy(c => c.OrdinalPosition).Select(c => new ColumnMetadataDto(
                c.ColumnName, c.DataType, c.IsNullable, c.IsPrimaryKey, c.IsForeignKey,
                c.OrdinalPosition, c.ForeignKeyTable, c.ForeignKeyColumn,
                c.DefaultValue, c.MaxLength, c.Description,
                DeserializeSampleValues(c.SampleValues),
                c.ForeignKeySchema, c.ForeignKeyConstraintName,
                c.SampleValuesComplete
            )
            {
                ForeignKeys = ColumnForeignKeys.Deserialize(c.ForeignKeys)
            }).ToList(),
            new List<IndexMetadataDto>(),
            m.TableDescription
        )).ToList();

        var refreshedAt = metadata.Count > 0 ? metadata.Max(m => m.LastRefreshed) : DateTime.UtcNow;
        var snapshot = new DatabaseMetadataSnapshot(dataSourceId, null, tables, refreshedAt);
        cache.Set(GetCacheKey(dataSourceId), snapshot, BeaconCacheEntries.For(CacheExpiration));
        return snapshot;
    }

    // The source's state that decides whether its catalog may be served: one by-id read of the columns it needs.
    private async Task<SourceState?> ReadStateAsync(int dataSourceId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.DataSources
            .Where(x => x.Id == dataSourceId)
            .Select(x =>
                new SourceState(x.DisabledTime, x.AllowMigrationWrites, x.DatabaseEngineType, x.EncryptedConnectionData))
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// <paramref name="column"/> without the foreign keys whose target schema, table or column carries a quote, bracket,
    /// semicolon or control character (<see cref="SqlIdentifierGuard.HasUnsafeCharacters"/>), which are named in
    /// <paramref name="skipped"/>. The single-target fields then name the first foreign key left, or none; the same instance
    /// comes back when nothing is left out.
    /// </summary>
    internal static ColumnMetadataDto WithoutUnsafeForeignKeyTargets(ColumnMetadataDto column, out IReadOnlyList<string> skipped)
    {
        var foreignKeys = ColumnForeignKeys.Of(column);
        var unsafeTargets = foreignKeys
            .Where(x => SqlIdentifierGuard.HasUnsafeCharacters(x.TargetSchema ?? string.Empty)
                || SqlIdentifierGuard.HasUnsafeCharacters(x.TargetTable)
                || SqlIdentifierGuard.HasUnsafeCharacters(x.TargetColumn))
            .ToList();
        skipped = unsafeTargets
            .Select(x => $"{x.TargetSchema}.{x.TargetTable}.{x.TargetColumn}")
            .ToList();
        if (unsafeTargets.Count == 0)
        {
            return column;
        }

        var safe = foreignKeys
            .Except(unsafeTargets)
            .ToList();
        var first = safe.FirstOrDefault();

        return column with
        {
            IsForeignKey = first != null,
            ForeignKeyTable = first?.TargetTable,
            ForeignKeyColumn = first?.TargetColumn,
            ForeignKeySchema = first?.TargetSchema,
            ForeignKeyConstraintName = first?.ConstraintName,
            ForeignKeys = column.ForeignKeys == null || safe.Count == 0 ? null : safe
        };
    }

    private static DatabaseMetadataSnapshot EmptySnapshot(int dataSourceId, DatabaseEngineType? engine) =>
        new(dataSourceId, engine, new List<TableMetadataDto>(), DateTime.UtcNow);

    private static List<string> ParseSchemaList(string? commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated)) return new List<string>();
        return commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string? SerializeSampleValues(IReadOnlyList<string>? sampleValues)
    {
        return sampleValues is { Count: > 0 }
            ? JsonSerializer.Serialize(sampleValues)
            : null;
    }

    private static IReadOnlyList<string>? DeserializeSampleValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            // Invalid JSON — treat as no samples
            return null;
        }
    }

    /// <summary>
    /// Clears the sample values stored for <paramref name="dataSourceId"/>'s columns, the columns of its archived tables
    /// included (the soft-delete filter on the tables is ignored): an archived data source, or one marked as a migration
    /// write target, is never read again, so nothing keeps a copy of its rows. The caller saves the change.
    /// </summary>
    internal static async Task ClearStoredSampleValuesAsync(BeaconContext context, int dataSourceId, CancellationToken cancellationToken)
    {
        var sampledColumns = await context.ColumnMetadata
            .IgnoreQueryFilters()
            .Where(x => x.DatabaseMetadata.DataSourceId == dataSourceId)
            .Where(x => x.SampleValues != null || x.SampleValuesComplete)
            .ToListAsync(cancellationToken);
        foreach (var column in sampledColumns)
        {
            column.SampleValues = null;
            column.SampleValuesComplete = false;
        }
    }

    internal static string GetCacheKey(int dataSourceId) => $"{CacheKeyPrefix}{dataSourceId}";

    private sealed record SourceState(DateTime? DisabledTime, bool AllowMigrationWrites, DatabaseEngineType? DatabaseEngineType, string EncryptedConnectionData)
    {
        public bool Readable => DisabledTime == null && !AllowMigrationWrites;

        // The compiler-generated ToString prints every member; the secrets are printed redacted.
        private bool PrintMembers(System.Text.StringBuilder builder)
        {
            builder.Append($"DisabledTime = {DisabledTime}, AllowMigrationWrites = {AllowMigrationWrites}, DatabaseEngineType = {DatabaseEngineType}, EncryptedConnectionData = {RedactedPrint.Of(EncryptedConnectionData)}, Readable = {Readable}");

            return true;
        }
    }
}
