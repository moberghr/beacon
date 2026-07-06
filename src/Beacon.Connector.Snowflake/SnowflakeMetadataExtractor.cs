using Dapper;
using Snowflake.Data.Client;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.Snowflake;

public class SnowflakeMetadataExtractor : IDatabaseMetadataExtractor
{
    public DatabaseEngineType SupportedEngineType => DatabaseEngineType.Snowflake;

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        using var connection = new SnowflakeDbConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        const string tablesQuery = @"
            SELECT
                TABLE_SCHEMA AS table_schema,
                TABLE_NAME AS table_name,
                COLUMN_NAME AS column_name,
                DATA_TYPE AS data_type,
                IS_NULLABLE AS is_nullable,
                COLUMN_DEFAULT AS column_default,
                CHARACTER_MAXIMUM_LENGTH AS character_maximum_length,
                ORDINAL_POSITION AS ordinal_position
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA != 'INFORMATION_SCHEMA'
            ORDER BY TABLE_SCHEMA, TABLE_NAME, ORDINAL_POSITION";

        var columnsData = (await connection.QueryAsync(new CommandDefinition(tablesQuery, commandTimeout: 180, cancellationToken: cancellationToken))).AsList();

        // Snowflake INFORMATION_SCHEMA doesn't expose PK/FK info directly via simple queries in all cases
        // Use SHOW commands or TABLE_CONSTRAINTS
        const string pkQuery = @"
            SELECT
                tc.TABLE_SCHEMA AS table_schema,
                tc.TABLE_NAME AS table_name,
                kcu.COLUMN_NAME AS column_name
            FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc
            JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE kcu
                ON tc.CONSTRAINT_NAME = kcu.CONSTRAINT_NAME
                AND tc.TABLE_SCHEMA = kcu.TABLE_SCHEMA
                AND tc.TABLE_NAME = kcu.TABLE_NAME
            WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY'";

        var pkData = (await connection.QueryAsync(new CommandDefinition(pkQuery, commandTimeout: 180, cancellationToken: cancellationToken))).AsList();
        var pkLookup = pkData
            .Select(pk => $"{pk.table_schema}.{pk.table_name}.{pk.column_name}")
            .ToHashSet();

        var tables = columnsData
            .GroupBy(c => new { schema = (string)c.table_schema, table = (string)c.table_name })
            .Select(g =>
            {
                var columns = g.Select(c =>
                {
                    var pkKey = $"{g.Key.schema}.{g.Key.table}.{c.column_name}";

                    return new ColumnMetadataDto(
                        ColumnName: (string)c.column_name,
                        DataType: (string)c.data_type,
                        IsNullable: ((string)c.is_nullable).Equals("YES", StringComparison.OrdinalIgnoreCase),
                        IsPrimaryKey: pkLookup.Contains(pkKey),
                        IsForeignKey: false,
                        OrdinalPosition: (int)c.ordinal_position,
                        ForeignKeyTable: null,
                        ForeignKeyColumn: null,
                        DefaultValue: c.column_default?.ToString(),
                        // CHARACTER_MAXIMUM_LENGTH is boxed as long/decimal, so `as int?` is always null — convert explicitly.
                        MaxLength: c.character_maximum_length == null ? (int?)null : Convert.ToInt32(c.character_maximum_length),
                        Description: null
                    );
                }).ToList();

                return new TableMetadataDto(
                    SchemaName: g.Key.schema,
                    TableName: g.Key.table,
                    Columns: columns,
                    Indexes: new List<IndexMetadataDto>(),
                    Description: null
                );
            })
            .ToList();

        return tables;
    }
}
