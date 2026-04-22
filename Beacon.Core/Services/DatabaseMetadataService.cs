using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Core.Services;

public class DatabaseMetadataService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEncryptionService encryptionService,
    IEnumerable<IDatabaseMetadataExtractor> metadataExtractors,
    IMemoryCache cache,
    ILogger<DatabaseMetadataService> logger)
    : IDatabaseMetadataService
{
    private const string CacheKeyPrefix = "DbMetadata_";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(1);

    // Lock per data source to prevent concurrent refresh operations
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _refreshLocks = new();

    public async Task<DatabaseMetadataSnapshot> RefreshMetadataAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        // Get or create a lock for this specific data source
        var refreshLock = _refreshLocks.GetOrAdd(dataSourceId, _ => new SemaphoreSlim(1, 1));

        // Try to acquire the lock - if already locked, wait
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            // Double-check cache after acquiring lock (another thread may have just completed refresh)
            if (cache.TryGetValue(GetCacheKey(dataSourceId), out DatabaseMetadataSnapshot? cachedSnapshot) && cachedSnapshot != null)
            {
                logger.LogDebug("Metadata for data source {DataSourceId} was refreshed by another thread", dataSourceId);
                return cachedSnapshot;
            }

            logger.LogInformation("Refreshing metadata for data source {DataSourceId}", dataSourceId);

            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken);
            if (dataSource == null)
                throw new BeaconException($"Data source {dataSourceId} not found");

            // Only database type data sources have metadata
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new BeaconException($"Data source {dataSourceId} is not a database type");

            // Extract metadata based on database type using registered extractors
            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            var extractor = metadataExtractors.FirstOrDefault(e => e.SupportedEngineType == dataSource.DatabaseEngineType.Value)
                ?? throw new NotSupportedException($"No metadata extractor registered for database type {dataSource.DatabaseEngineType}. Make sure to register the appropriate connector.");
            var tables = await extractor.ExtractMetadataAsync(connectionString, cancellationToken);

            // Apply per-datasource filters
            tables = ApplyMetadataFilters(tables, dataSource);

            // Store in database
            await StoreMetadataAsync(dataSourceId, tables, cancellationToken);

            // Update cache
            var snapshot = new DatabaseMetadataSnapshot(dataSourceId, dataSource.DatabaseEngineType.Value, tables, DateTime.UtcNow);
            cache.Set(GetCacheKey(dataSourceId), snapshot, CacheExpiration);

            logger.LogInformation("Refreshed metadata for data source {DataSourceId}: {TableCount} tables", dataSourceId, tables.Count);

            return snapshot;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    public async Task<DatabaseMetadataSnapshot> GetMetadataAsync(int dataSourceId, CancellationToken cancellationToken = default)
    {
        // Try to get from cache first
        if (cache.TryGetValue(GetCacheKey(dataSourceId), out DatabaseMetadataSnapshot? cachedSnapshot) && cachedSnapshot != null)
        {
            return cachedSnapshot;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get the data source to know the database type and metadata loading options
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken);
        if (dataSource == null)
            throw new BeaconException($"Data source {dataSourceId} not found");

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
            metadataQuery = metadataQuery.Take(dataSource.MetadataMaxTables);
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
            metadata = await metadataQuery
                .Include(m => m.Columns)
                .Include(m => m.Indexes)
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
                        c.Description
                    )).ToList(),
                loadTableNamesOnly ? new List<IndexMetadataDto>() : m.Indexes.Select(i => new IndexMetadataDto(
                    i.IndexName,
                    i.IsUnique,
                    i.IsPrimaryKey,
                    i.Columns
                )).ToList(),
                m.TableDescription
            )).ToList();

            var snapshot = new DatabaseMetadataSnapshot(dataSourceId, dataSource.DatabaseEngineType.Value, tables, metadata.Max(m => m.LastRefreshed));
            cache.Set(GetCacheKey(dataSourceId), snapshot, CacheExpiration);

            return snapshot;
        }

        // If no data in database, refresh from source
        return await RefreshMetadataAsync(dataSourceId, cancellationToken);
    }

    public async Task<IEnumerable<string>> GetTableNamesAsync(int dataSourceId, string? schemaName = null, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(dataSourceId, cancellationToken);

        var tables = metadata.Tables.AsEnumerable();
        if (!string.IsNullOrEmpty(schemaName))
        {
            tables = tables.Where(t => t.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase));
        }

        return tables.Select(t => t.TableName).OrderBy(t => t);
    }

    public async Task<IEnumerable<ColumnMetadataDto>> GetColumnsAsync(int dataSourceId, string tableName, string? schemaName = null, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(dataSourceId, cancellationToken);

        var table = metadata.Tables.FirstOrDefault(t =>
            t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            (schemaName == null || t.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase))
        );

        return table?.Columns ?? Enumerable.Empty<ColumnMetadataDto>();
    }

    private async Task StoreMetadataAsync(int dataSourceId, IReadOnlyList<TableMetadataDto> tables, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Remove existing metadata for this data source
        var existingMetadata = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .ToListAsync(cancellationToken);

        context.DatabaseMetadata.RemoveRange(existingMetadata);

        // Save changes to commit the deletions before adding new records
        // This prevents unique constraint violations on (DataSourceId, SchemaName, TableName)
        await context.SaveChangesAsync(cancellationToken);

        // Add new metadata
        foreach (var table in tables)
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
                    DefaultValue = column.DefaultValue,
                    MaxLength = column.MaxLength,
                    Description = column.Description
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
            .Where(m => m.DataSourceId == dataSourceId && m.ArchivedTime == null)
            .Include(m => m.Columns)
            .ToListAsync(cancellationToken);

        var tables = metadata.Select(m => new TableMetadataDto(
            m.SchemaName,
            m.TableName,
            m.Columns.OrderBy(c => c.OrdinalPosition).Select(c => new ColumnMetadataDto(
                c.ColumnName, c.DataType, c.IsNullable, c.IsPrimaryKey, c.IsForeignKey,
                c.OrdinalPosition, c.ForeignKeyTable, c.ForeignKeyColumn,
                c.DefaultValue, c.MaxLength, c.Description
            )).ToList(),
            new List<IndexMetadataDto>(),
            m.TableDescription
        )).ToList();

        var refreshedAt = metadata.Count > 0 ? metadata.Max(m => m.LastRefreshed) : DateTime.UtcNow;
        var snapshot = new DatabaseMetadataSnapshot(dataSourceId, null, tables, refreshedAt);
        cache.Set(GetCacheKey(dataSourceId), snapshot, CacheExpiration);
        return snapshot;
    }

    private static List<string> ParseSchemaList(string? commaSeparated)
    {
        if (string.IsNullOrWhiteSpace(commaSeparated)) return new List<string>();
        return commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string GetCacheKey(int dataSourceId) => $"{CacheKeyPrefix}{dataSourceId}";
}
