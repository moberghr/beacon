using Dapper;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.MySql;

public class MySqlMetadataExtractor : IDatabaseMetadataExtractor
{
    public DatabaseEngineType SupportedEngineType => DatabaseEngineType.MySQL;

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        // Through the factory, as every other connection to the source: the same pool and connect timeout.
        await using var connection = DbConnectionFactory.CreateConnection(DatabaseEngineType.MySQL, connectionString);
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

        var columnsData = (await connection.QueryAsync(new CommandDefinition(tablesQuery, new { DatabaseName = databaseName }, commandTimeout: 180, cancellationToken: cancellationToken))).AsList();

        const string foreignKeysQuery = @"
            SELECT
                TABLE_SCHEMA AS table_schema,
                TABLE_NAME AS table_name,
                COLUMN_NAME AS column_name,
                REFERENCED_TABLE_SCHEMA AS foreign_schema_name,
                REFERENCED_TABLE_NAME AS foreign_table_name,
                REFERENCED_COLUMN_NAME AS foreign_column_name,
                CONSTRAINT_NAME AS constraint_name
            FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE
            WHERE TABLE_SCHEMA = @DatabaseName
              AND REFERENCED_TABLE_NAME IS NOT NULL
            ORDER BY constraint_name";

        var foreignKeys = (await connection.QueryAsync(new CommandDefinition(foreignKeysQuery, new { DatabaseName = databaseName }, commandTimeout: 180, cancellationToken: cancellationToken))).AsList();
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

        var indexesData = (await connection.QueryAsync(new CommandDefinition(indexesQuery, new { DatabaseName = databaseName }, commandTimeout: 180, cancellationToken: cancellationToken))).AsList();

        return Assemble(columnsData, foreignKeys, indexesData);
    }

    /// <summary>
    /// Builds the table list from the rows the catalog queries returned. Pure: no connection, so a fixture can pin it.
    /// </summary>
    internal static IReadOnlyList<TableMetadataDto> Assemble(IEnumerable<dynamic> columnsData, IEnumerable<dynamic> foreignKeys, IEnumerable<dynamic> indexesData)
    {
        // Every foreign key of a column, in constraint-name order: the first fills the single-target fields.
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => ColumnForeignKeys.InConstraintOrder(g.Select(fk => new ColumnForeignKeyReference(
                    ConstraintName: (string?)fk.constraint_name,
                    TargetSchema: (string?)fk.foreign_schema_name,
                    TargetTable: (string)fk.foreign_table_name,
                    TargetColumn: (string)fk.foreign_column_name)))
            );

        var tables = columnsData
            .GroupBy(c => new { schema = (string)c.table_schema, table = (string)c.table_name })
            .Select(g =>
            {
                var columns = g.Select(c =>
                {
                    var fkKey = $"{g.Key.schema}.{g.Key.table}.{c.column_name}";
                    var hasFk = fkLookup.TryGetValue(fkKey, out var foreignKeyList);
                    var fkInfo = hasFk ? foreignKeyList![0] : null;

                    return new ColumnMetadataDto(
                        ColumnName: (string)c.column_name,
                        DataType: (string)c.data_type,
                        IsNullable: ((string)c.is_nullable).Equals("YES", StringComparison.OrdinalIgnoreCase),
                        IsPrimaryKey: ((string)c.column_key) == "PRI",
                        IsForeignKey: hasFk,
                        OrdinalPosition: (int)c.ordinal_position,
                        ForeignKeyTable: fkInfo?.TargetTable,
                        ForeignKeyColumn: fkInfo?.TargetColumn,
                        DefaultValue: c.column_default?.ToString(),
                        // CHARACTER_MAXIMUM_LENGTH is boxed as long, so `as int?` is always null — convert explicitly.
                        MaxLength: c.character_maximum_length == null ? (int?)null : Convert.ToInt32(c.character_maximum_length),
                        Description: null,
                        SampleValues: null,
                        ForeignKeySchema: fkInfo?.TargetSchema,
                        ForeignKeyConstraintName: fkInfo?.ConstraintName
                    )
                    {
                        ForeignKeys = foreignKeyList
                    };
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
