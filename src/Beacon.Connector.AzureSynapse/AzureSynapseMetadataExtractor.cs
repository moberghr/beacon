using Dapper;
using Microsoft.Data.SqlClient;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.AzureSynapse;

public class AzureSynapseMetadataExtractor : IDatabaseMetadataExtractor
{
    public DatabaseEngineType SupportedEngineType => DatabaseEngineType.AzureSynapse;

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

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

        var columnsData = await connection.QueryAsync(new CommandDefinition(tablesQuery, commandTimeout: 180, cancellationToken: cancellationToken));

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

        var foreignKeys = await connection.QueryAsync(new CommandDefinition(foreignKeysQuery, commandTimeout: 180, cancellationToken: cancellationToken));
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => (TableName: (string)g.First().foreign_table_name, ColumnName: (string)g.First().foreign_column_name)
            );

        // Synapse may not support STRING_AGG in all configurations, use simpler index query
        const string indexesQuery = @"
            SELECT
                s.name AS table_schema,
                t.name AS table_name,
                i.name AS index_name,
                i.is_unique,
                i.is_primary_key,
                c.name AS column_name,
                ic.key_ordinal
            FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.index_columns ic ON i.object_id = ic.object_id AND i.index_id = ic.index_id
            JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
            WHERE i.name IS NOT NULL
            ORDER BY s.name, t.name, i.name, ic.key_ordinal";

        var indexesData = (await connection.QueryAsync(new CommandDefinition(indexesQuery, commandTimeout: 180, cancellationToken: cancellationToken))).AsList();

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
                        // sys.columns.max_length is boxed as short, so `as int?` is always null — convert explicitly.
                        // Note: for nvarchar/nchar this value is in bytes (2x the character length).
                        MaxLength: c.max_length == null ? (int?)null : Convert.ToInt32(c.max_length),
                        Description: null
                    );
                }).ToList();

                var indexes = indexesData
                    .Where(i => (string)i.table_schema == g.Key.schema && (string)i.table_name == g.Key.table)
                    .GroupBy(i => new { name = (string)i.index_name, is_unique = (bool)i.is_unique, is_pk = (bool)i.is_primary_key })
                    .Select(ig => new IndexMetadataDto(
                        IndexName: ig.Key.name,
                        IsUnique: ig.Key.is_unique,
                        IsPrimaryKey: ig.Key.is_pk,
                        Columns: ig.OrderBy(x => (int)x.key_ordinal).Select(x => (string)x.column_name).ToArray()
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
}
