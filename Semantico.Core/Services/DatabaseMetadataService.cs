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

public class DatabaseMetadataService : IDatabaseMetadataService
{
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DatabaseMetadataService> _logger;

    private const string CacheKeyPrefix = "DbMetadata_";
    private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(1);

    public DatabaseMetadataService(
        IDbContextFactory<SemanticoContext> contextFactory,
        IMemoryCache cache,
        ILogger<DatabaseMetadataService> logger)
    {
        _contextFactory = contextFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<DatabaseMetadataSnapshot> RefreshMetadataAsync(int projectId, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Refreshing metadata for project {ProjectId}", projectId);

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var project = await context.Projects.FindAsync(new object[] { projectId }, cancellationToken);
        if (project == null)
            throw new SemanticoException($"Project {projectId} not found");

        // Extract metadata based on database type
        var tables = project.DatabaseEngineType switch
        {
            DatabaseEngineType.PostgreSQL => await GetPostgreSqlMetadataAsync(project.ConnectionString, cancellationToken),
            DatabaseEngineType.MSSQL => await GetSqlServerMetadataAsync(project.ConnectionString, cancellationToken),
            _ => throw new NotSupportedException($"Database type {project.DatabaseEngineType} not supported for metadata extraction")
        };

        // Store in database
        await StoreMetadataAsync(projectId, tables, cancellationToken);

        // Update cache
        var snapshot = new DatabaseMetadataSnapshot(projectId, tables, DateTime.UtcNow);
        _cache.Set(GetCacheKey(projectId), snapshot, CacheExpiration);

        _logger.LogInformation("Refreshed metadata for project {ProjectId}: {TableCount} tables", projectId, tables.Count);

        return snapshot;
    }

    public async Task<DatabaseMetadataSnapshot> GetMetadataAsync(int projectId, CancellationToken cancellationToken = default)
    {
        // Try to get from cache first
        if (_cache.TryGetValue(GetCacheKey(projectId), out DatabaseMetadataSnapshot? cachedSnapshot) && cachedSnapshot != null)
        {
            _logger.LogDebug("Returning cached metadata for project {ProjectId}", projectId);
            return cachedSnapshot;
        }

        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Try to get from database
        var metadata = await context.DatabaseMetadata
            .Include(m => m.Columns)
            .Include(m => m.Indexes)
            .Where(m => m.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        if (metadata.Any())
        {
            var tables = metadata.Select(m => new TableMetadataDto(
                m.SchemaName,
                m.TableName,
                m.Columns.Select(c => new ColumnMetadataDto(
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
                )).OrderBy(c => c.OrdinalPosition).ToList(),
                m.Indexes.Select(i => new IndexMetadataDto(
                    i.IndexName,
                    i.IsUnique,
                    i.IsPrimaryKey,
                    i.Columns
                )).ToList(),
                m.TableDescription
            )).ToList();

            var snapshot = new DatabaseMetadataSnapshot(projectId, tables, metadata.Max(m => m.LastRefreshed));
            _cache.Set(GetCacheKey(projectId), snapshot, CacheExpiration);

            return snapshot;
        }

        // If no data in database, refresh from source
        return await RefreshMetadataAsync(projectId, cancellationToken);
    }

    public async Task<IEnumerable<string>> GetTableNamesAsync(int projectId, string? schemaName = null, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(projectId, cancellationToken);

        var tables = metadata.Tables.AsEnumerable();
        if (!string.IsNullOrEmpty(schemaName))
        {
            tables = tables.Where(t => t.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase));
        }

        return tables.Select(t => t.TableName).OrderBy(t => t);
    }

    public async Task<IEnumerable<ColumnMetadataDto>> GetColumnsAsync(int projectId, string tableName, string? schemaName = null, CancellationToken cancellationToken = default)
    {
        var metadata = await GetMetadataAsync(projectId, cancellationToken);

        var table = metadata.Tables.FirstOrDefault(t =>
            t.TableName.Equals(tableName, StringComparison.OrdinalIgnoreCase) &&
            (schemaName == null || t.SchemaName.Equals(schemaName, StringComparison.OrdinalIgnoreCase))
        );

        return table?.Columns ?? Enumerable.Empty<ColumnMetadataDto>();
    }

    private async Task<IReadOnlyList<TableMetadataDto>> GetPostgreSqlMetadataAsync(string connectionString, CancellationToken cancellationToken)
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        // Get tables and columns
        const string tablesQuery = @"
            SELECT
                t.table_schema,
                t.table_name,
                c.column_name,
                c.data_type,
                c.is_nullable,
                c.column_default,
                c.character_maximum_length,
                c.ordinal_position,
                CASE WHEN pk.column_name IS NOT NULL THEN true ELSE false END as is_primary_key
            FROM information_schema.tables t
            JOIN information_schema.columns c
                ON t.table_schema = c.table_schema
                AND t.table_name = c.table_name
            LEFT JOIN (
                SELECT ku.table_schema, ku.table_name, ku.column_name
                FROM information_schema.table_constraints tc
                JOIN information_schema.key_column_usage ku
                    ON tc.constraint_name = ku.constraint_name
                    AND tc.table_schema = ku.table_schema
                WHERE tc.constraint_type = 'PRIMARY KEY'
            ) pk ON c.table_schema = pk.table_schema
                AND c.table_name = pk.table_name
                AND c.column_name = pk.column_name
            WHERE t.table_schema NOT IN ('pg_catalog', 'information_schema')
                AND t.table_type = 'BASE TABLE'
            ORDER BY t.table_schema, t.table_name, c.ordinal_position";

        var columnsData = await connection.QueryAsync(tablesQuery);

        // Get foreign keys
        const string foreignKeysQuery = @"
            SELECT
                tc.table_schema,
                tc.table_name,
                kcu.column_name,
                ccu.table_name AS foreign_table_name,
                ccu.column_name AS foreign_column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
                ON tc.constraint_name = kcu.constraint_name
                AND tc.table_schema = kcu.table_schema
            JOIN information_schema.constraint_column_usage ccu
                ON ccu.constraint_name = tc.constraint_name
                AND ccu.table_schema = tc.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
                AND tc.table_schema NOT IN ('pg_catalog', 'information_schema')";

        var foreignKeys = await connection.QueryAsync(foreignKeysQuery);
        var fkLookup = foreignKeys.ToDictionary(
            fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}",
            fk => (TableName: (string)fk.foreign_table_name, ColumnName: (string)fk.foreign_column_name)
        );

        // Get indexes
        const string indexesQuery = @"
            SELECT
                schemaname AS table_schema,
                tablename AS table_name,
                indexname AS index_name,
                indexdef
            FROM pg_indexes
            WHERE schemaname NOT IN ('pg_catalog', 'information_schema')
            ORDER BY schemaname, tablename, indexname";

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
            LEFT JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id AND c.column_id = ic.column_id
            ORDER BY s.name, t.name, c.column_id";

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
        var fkLookup = foreignKeys.ToDictionary(
            fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}",
            fk => (TableName: (string)fk.foreign_table_name, ColumnName: (string)fk.foreign_column_name)
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

    private async Task StoreMetadataAsync(int projectId, IReadOnlyList<TableMetadataDto> tables, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Remove existing metadata for this project
        var existingMetadata = await context.DatabaseMetadata
            .Where(m => m.ProjectId == projectId)
            .ToListAsync(cancellationToken);

        context.DatabaseMetadata.RemoveRange(existingMetadata);

        // Add new metadata
        foreach (var table in tables)
        {
            var metadata = new DatabaseMetadata
            {
                ProjectId = projectId,
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

    private static string GetCacheKey(int projectId) => $"{CacheKeyPrefix}{projectId}";
}
