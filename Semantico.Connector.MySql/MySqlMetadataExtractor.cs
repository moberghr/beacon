using Dapper;
using MySql.Data.MySqlClient;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.Metadata;
using Semantico.Core.Services.Providers;

namespace Semantico.Connector.MySql;

public class MySqlMetadataExtractor : IDatabaseMetadataExtractor
{
    public DatabaseEngineType SupportedEngineType => DatabaseEngineType.MySQL;

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var databaseName = connection.Database;

        const string tablesQuery = @"
            SELECT
                TABLE_SCHEMA AS table_schema,
                TABLE_NAME AS table_name,
                COLUMN_NAME AS column_name,
                DATA_TYPE AS data_type,
                IS_NULLABLE AS is_nullable,
                COLUMN_DEFAULT AS column_default,
                CHARACTER_MAXIMUM_LENGTH AS character_maximum_length,
                ORDINAL_POSITION AS ordinal_position,
                COLUMN_KEY AS column_key
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = @DatabaseName
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

        var columnsData = (await connection.QueryAsync(tablesQuery, new { DatabaseName = databaseName }, commandTimeout: 180)).AsList();

        const string foreignKeysQuery = @"
            SELECT
                TABLE_SCHEMA AS table_schema,
                TABLE_NAME AS table_name,
                COLUMN_NAME AS column_name,
                REFERENCED_TABLE_NAME AS foreign_table_name,
                REFERENCED_COLUMN_NAME AS foreign_column_name
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @DatabaseName
              AND REFERENCED_TABLE_NAME IS NOT NULL";

        var foreignKeys = (await connection.QueryAsync(foreignKeysQuery, new { DatabaseName = databaseName }, commandTimeout: 180)).AsList();
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => (TableName: (string)g.First().foreign_table_name, ColumnName: (string)g.First().foreign_column_name)
            );

        const string indexesQuery = @"
            SELECT
                TABLE_SCHEMA AS table_schema,
                TABLE_NAME AS table_name,
                INDEX_NAME AS index_name,
                NON_UNIQUE AS non_unique,
                GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX) AS column_names
            FROM INFORMATION_SCHEMA.STATISTICS
            WHERE TABLE_SCHEMA = @DatabaseName
            GROUP BY TABLE_SCHEMA, TABLE_NAME, INDEX_NAME, NON_UNIQUE";

        var indexesData = (await connection.QueryAsync(indexesQuery, new { DatabaseName = databaseName }, commandTimeout: 180)).AsList();

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
                        IsPrimaryKey: ((string)c.column_key) == "PRI",
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
                    .Select(i => new IndexMetadataDto(
                        IndexName: (string)i.index_name,
                        IsUnique: (long)i.non_unique == 0,
                        IsPrimaryKey: ((string)i.index_name) == "PRIMARY",
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
}
