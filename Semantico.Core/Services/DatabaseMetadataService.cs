using System.Data;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Npgsql;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.Metadata;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models;
using Semantico.Core.Models.Metadata;
using System.Data.SqlClient;

namespace Semantico.Core.Services;

public class DatabaseMetadataService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IEncryptionService encryptionService,
    IMemoryCache cache,
    SemanticoConfiguration configuration,
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
                throw new SemanticoException($"Data source {dataSourceId} not found");

            // Only database type data sources have metadata
            if (!dataSource.DatabaseEngineType.HasValue)
                throw new SemanticoException($"Data source {dataSourceId} is not a database type");

            // Extract metadata based on database type
            var connectionString = encryptionService.Decrypt(dataSource.EncryptedConnectionData);
            var tables = dataSource.DatabaseEngineType.Value switch
            {
                DatabaseEngineType.PostgreSQL => await GetPostgreSqlMetadataAsync(connectionString, cancellationToken),
                DatabaseEngineType.MSSQL => await GetSqlServerMetadataAsync(connectionString, cancellationToken),
                _ => throw new NotSupportedException($"Database type {dataSource.DatabaseEngineType} not supported for metadata extraction")
            };

            // Apply configuration filters
            tables = ApplyMetadataFilters(tables);

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
        // Check if metadata loading is disabled
        if (!configuration.MetadataLoading.Enabled)
        {
            logger.LogDebug("Metadata loading is disabled in configuration");
            await using var ctx = await contextFactory.CreateDbContextAsync(cancellationToken);
            var ds = await ctx.DataSources.FirstOrDefaultAsync(d => d.Id == dataSourceId, cancellationToken);
            if (ds?.DatabaseEngineType == null)
                throw new SemanticoException($"Data source {dataSourceId} not found or is not a database type");

            return new DatabaseMetadataSnapshot(dataSourceId, ds.DatabaseEngineType.Value, new List<TableMetadataDto>(), DateTime.UtcNow);
        }

        // Try to get from cache first
        if (cache.TryGetValue(GetCacheKey(dataSourceId), out DatabaseMetadataSnapshot? cachedSnapshot) && cachedSnapshot != null)
        {
            return cachedSnapshot;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get the data source to know the database type
        var dataSource = await context.DataSources.FirstOrDefaultAsync(ds => ds.Id == dataSourceId, cancellationToken);
        if (dataSource == null)
            throw new SemanticoException($"Data source {dataSourceId} not found");

        if (!dataSource.DatabaseEngineType.HasValue)
            throw new SemanticoException($"Data source {dataSourceId} is not a database type");

        // Try to get from database
        var metadataQuery = context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId);

        // Apply schema filters
        var options = configuration.MetadataLoading;
        if (options.IncludeSchemas.Any())
        {
            var includeSchemas = options.IncludeSchemas.Select(s => s.ToLowerInvariant()).ToList();
            metadataQuery = metadataQuery.Where(m => includeSchemas.Contains(m.SchemaName.ToLower()));
        }
        else if (options.ExcludeSchemas.Any())
        {
            var excludeSchemas = options.ExcludeSchemas.Select(s => s.ToLowerInvariant()).ToList();
            metadataQuery = metadataQuery.Where(m => !excludeSchemas.Contains(m.SchemaName.ToLower()));
        }

        // Apply table limit
        if (options.MaxTables > 0)
        {
            metadataQuery = metadataQuery.Take(options.MaxTables);
            logger.LogDebug("Limiting metadata to {MaxTables} tables", options.MaxTables);
        }

        // Load metadata based on LoadTableNamesOnly setting
        List<DatabaseMetadata> metadata;
        if (options.LoadTableNamesOnly)
        {
            metadata = await metadataQuery.ToListAsync(cancellationToken);
            logger.LogDebug("Loaded {Count} table names only (columns excluded)", metadata.Count);
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
            var tables = metadata.Select(m => new TableMetadataDto(
                m.SchemaName,
                m.TableName,
                options.LoadTableNamesOnly ? new List<ColumnMetadataDto>() : m.Columns
                    .OrderBy(c => c.OrdinalPosition)
                    .Take(options.MaxColumnsPerTable > 0 ? options.MaxColumnsPerTable : int.MaxValue)
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
                options.LoadTableNamesOnly ? new List<IndexMetadataDto>() : m.Indexes.Select(i => new IndexMetadataDto(
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

    private async Task<IReadOnlyList<TableMetadataDto>> GetPostgreSqlMetadataAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Get tables and columns using pg_catalog for better performance
        const string tablesQuery = @"
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                a.attname AS column_name,
                pg_catalog.format_type(a.atttypid, a.atttypmod) AS data_type,
                CASE WHEN a.attnotnull THEN 'NO' ELSE 'YES' END AS is_nullable,
                pg_get_expr(ad.adbin, ad.adrelid) AS column_default,
                CASE
                    WHEN t.typname IN ('varchar', 'char', 'bpchar') THEN
                        CASE WHEN a.atttypmod > 0 THEN a.atttypmod - 4 ELSE NULL END
                    ELSE NULL
                END AS character_maximum_length,
                a.attnum AS ordinal_position,
                EXISTS(
                    SELECT 1
                    FROM pg_constraint con
                    WHERE con.conrelid = c.oid
                    AND con.contype = 'p'
                    AND a.attnum = ANY(con.conkey)
                ) AS is_primary_key
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            JOIN pg_type t ON a.atttypid = t.oid
            LEFT JOIN pg_attrdef ad ON a.attrelid = ad.adrelid AND a.attnum = ad.adnum
            WHERE c.relkind = 'r'
                AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                AND a.attnum > 0
                AND NOT a.attisdropped";

        var columnsData = await connection.QueryAsync(tablesQuery, commandTimeout:180);

        // Get foreign keys using pg_catalog for better performance
        const string foreignKeysQuery = @"
            SELECT
                n.nspname AS table_schema,
                c.relname AS table_name,
                a.attname AS column_name,
                fn.nspname AS foreign_schema_name,
                fc.relname AS foreign_table_name,
                fa.attname AS foreign_column_name
            FROM pg_constraint con
            JOIN pg_class c ON con.conrelid = c.oid
            JOIN pg_namespace n ON c.relnamespace = n.oid
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = ANY(con.conkey)
            JOIN pg_class fc ON con.confrelid = fc.oid
            JOIN pg_namespace fn ON fc.relnamespace = fn.oid
            JOIN pg_attribute fa ON fa.attrelid = fc.oid AND fa.attnum = ANY(con.confkey)
            WHERE con.contype = 'f'
                AND n.nspname NOT IN ('pg_catalog', 'information_schema')
                AND array_position(con.conkey, a.attnum) = array_position(con.confkey, fa.attnum)";

        var foreignKeys = await connection.QueryAsync(foreignKeysQuery, commandTimeout:180);
        // Use GroupBy + First to handle duplicate keys (can happen with multiple FK constraints on same column)
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => (TableName: (string)g.First().foreign_table_name, ColumnName: (string)g.First().foreign_column_name)
            );

        // Get indexes
        const string indexesQuery = @"
            SELECT
                schemaname AS table_schema,
                tablename AS table_name,
                indexname AS index_name,
                indexdef
            FROM pg_indexes
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')";

        var indexesData = await connection.QueryAsync(indexesQuery, commandTimeout:180);

        // Group by table
        var tables = columnsData
            .GroupBy(c => new { schema = (string)c.table_schema, table = (string)c.table_name })
            .Select(g =>
            {
                var columns = g.Select(c =>
                {
                    var fkKey = $"{g.Key.schema}.{g.Key.table}.{c.column_name}";
                    var hasFk = fkLookup.TryGetValue(fkKey, out var fkInfo);

                    return new ColumnMetadataDto(
                        ColumnName: (string)c.column_name,
                        DataType: (string)c.data_type,
                        IsNullable: ((string)c.is_nullable).Equals("YES", StringComparison.OrdinalIgnoreCase),
                        IsPrimaryKey: (bool)c.is_primary_key,
                        IsForeignKey: hasFk,
                        OrdinalPosition: (int)c.ordinal_position,
                        ForeignKeyTable: hasFk ? fkInfo.TableName : null,
                        ForeignKeyColumn: hasFk ? fkInfo.ColumnName : null,
                        DefaultValue: c.column_default?.ToString(),
                        MaxLength: c.character_maximum_length as int?,
                        Description: null
                    );
                }).ToList();

                var indexes = indexesData
                    .Where(i => (string)i.table_schema == g.Key.schema && (string)i.table_name == g.Key.table)
                    .Select(i =>
                    {
                        var indexDef = (string)i.indexdef;
                        var isPrimaryKey = indexDef.Contains("PRIMARY KEY", StringComparison.OrdinalIgnoreCase);
                        var isUnique = indexDef.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase) || isPrimaryKey;

                        // Extract column names from index definition
                        var columnsMatch = System.Text.RegularExpressions.Regex.Match(indexDef, @"\((.*?)\)");
                        var columnNames = columnsMatch.Success
                            ? columnsMatch.Groups[1].Value.Split(',').Select(c => c.Trim()).ToArray()
                            : Array.Empty<string>();

                        return new IndexMetadataDto(
                            IndexName: (string)i.index_name,
                            IsUnique: isUnique,
                            IsPrimaryKey: isPrimaryKey,
                            Columns: columnNames
                        );
                    }).ToList();

                return new TableMetadataDto(
                    SchemaName: g.Key.schema,
                    TableName: g.Key.table,
                    Columns: columns,
                    Indexes: indexes,
                    Description: null
                );
            })
            .ToList();

        return tables;
    }

    private async Task<IReadOnlyList<TableMetadataDto>> GetSqlServerMetadataAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Get tables and columns
        const string tablesQuery = @"
            SELECT
                s.name AS table_schema,
                t.name AS table_name,
                c.name AS column_name,
                ty.name AS data_type,
                c.is_nullable,
                c.max_length,
                c.column_id AS ordinal_position,
                CASE WHEN ic.column_id IS NOT NULL THEN 1 ELSE 0 END as is_primary_key
            FROM sys.tables t
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.columns c ON t.object_id = c.object_id
            JOIN sys.types ty ON c.user_type_id = ty.user_type_id
            LEFT JOIN sys.indexes i ON t.object_id = i.object_id AND i.is_primary_key = 1
            LEFT JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id AND c.column_id = ic.column_id";

        var columnsData = await connection.QueryAsync(tablesQuery);

        // Get foreign keys
        const string foreignKeysQuery = @"
            SELECT
                s.name AS table_schema,
                t.name AS table_name,
                c.name AS column_name,
                rt.name AS foreign_table_name,
                rc.name AS foreign_column_name
            FROM sys.foreign_key_columns fk
            JOIN sys.tables t ON fk.parent_object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.columns c ON fk.parent_object_id = c.object_id AND fk.parent_column_id = c.column_id
            JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
            JOIN sys.columns rc ON fk.referenced_object_id = rc.object_id AND fk.referenced_column_id = rc.column_id";

        var foreignKeys = await connection.QueryAsync(foreignKeysQuery);
        // Use GroupBy + First to handle duplicate keys (can happen with multiple FK constraints on same column)
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => (TableName: (string)g.First().foreign_table_name, ColumnName: (string)g.First().foreign_column_name)
            );

        // Get indexes
        const string indexesQuery = @"
            SELECT
                s.name AS table_schema,
                t.name AS table_name,
                i.name AS index_name,
                i.is_unique,
                i.is_primary_key,
                STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal) AS column_names
            FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.name IS NOT NULL
            GROUP BY s.name, t.name, i.name, i.is_unique, i.is_primary_key";

        var indexesData = await connection.QueryAsync(indexesQuery);

        // Group by table
        var tables = columnsData
            .GroupBy(c => new { schema = (string)c.table_schema, table = (string)c.table_name })
            .Select(g =>
            {
                var columns = g.Select(c =>
                {
                    var fkKey = $"{g.Key.schema}.{g.Key.table}.{c.column_name}";
                    var hasFk = fkLookup.TryGetValue(fkKey, out var fkInfo);

                    return new ColumnMetadataDto(
                        ColumnName: (string)c.column_name,
                        DataType: (string)c.data_type,
                        IsNullable: (bool)c.is_nullable,
                        IsPrimaryKey: (int)c.is_primary_key == 1,
                        IsForeignKey: hasFk,
                        OrdinalPosition: (int)c.ordinal_position,
                        ForeignKeyTable: hasFk ? fkInfo.TableName : null,
                        ForeignKeyColumn: hasFk ? fkInfo.ColumnName : null,
                        DefaultValue: null,
                        MaxLength: c.max_length as int?,
                        Description: null
                    );
                }).ToList();

                var indexes = indexesData
                    .Where(i => (string)i.table_schema == g.Key.schema && (string)i.table_name == g.Key.table)
                    .Select(i => new IndexMetadataDto(
                        IndexName: (string)i.index_name,
                        IsUnique: (bool)i.is_unique,
                        IsPrimaryKey: (bool)i.is_primary_key,
                        Columns: ((string)i.column_names).Split(',')
                    )).ToList();

                return new TableMetadataDto(
                    SchemaName: g.Key.schema,
                    TableName: g.Key.table,
                    Columns: columns,
                    Indexes: indexes,
                    Description: null
                );
            })
            .ToList();

        return tables;
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
    /// Applies configuration filters to metadata to control memory usage.
    /// </summary>
    private IReadOnlyList<TableMetadataDto> ApplyMetadataFilters(IReadOnlyList<TableMetadataDto> tables)
    {
        var options = configuration.MetadataLoading;
        var filteredTables = tables.AsEnumerable();

        // Apply schema filters
        if (options.IncludeSchemas.Any())
        {
            var includeSchemas = options.IncludeSchemas.Select(s => s.ToLowerInvariant()).ToHashSet();
            filteredTables = filteredTables.Where(t => includeSchemas.Contains(t.SchemaName.ToLowerInvariant()));
            logger.LogDebug("Filtering metadata to include only schemas: {Schemas}", string.Join(", ", options.IncludeSchemas));
        }
        else if (options.ExcludeSchemas.Any())
        {
            var excludeSchemas = options.ExcludeSchemas.Select(s => s.ToLowerInvariant()).ToHashSet();
            filteredTables = filteredTables.Where(t => !excludeSchemas.Contains(t.SchemaName.ToLowerInvariant()));
            logger.LogDebug("Filtering metadata to exclude schemas: {Schemas}", string.Join(", ", options.ExcludeSchemas));
        }

        // Apply table limit
        if (options.MaxTables > 0)
        {
            filteredTables = filteredTables.Take(options.MaxTables);
            logger.LogDebug("Limiting metadata to {MaxTables} tables", options.MaxTables);
        }

        var result = filteredTables.ToList();

        // Apply column limit or load table names only
        if (options.LoadTableNamesOnly)
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

        if (options.MaxColumnsPerTable > 0)
        {
            logger.LogDebug("Limiting columns to {MaxColumns} per table", options.MaxColumnsPerTable);
            return result.Select(t => new TableMetadataDto(
                t.SchemaName,
                t.TableName,
                t.Columns.Take(options.MaxColumnsPerTable).ToList(),
                t.Indexes,
                t.Description
            )).ToList();
        }

        return result;
    }

    private static string GetCacheKey(int dataSourceId) => $"{CacheKeyPrefix}{dataSourceId}";
}
