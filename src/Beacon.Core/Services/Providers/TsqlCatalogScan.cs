using System.Data.Common;
using Beacon.Core.Models.Metadata;
using Dapper;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// The catalog scan the T-SQL connectors (SQL Server, Synapse) share: three fixed catalog statements, then a pure
/// row-to-DTO assembly. Internal to Core (visible to those two connectors), so it adds no public API (§2.4).
/// The statements are fixed text the connector passes in and run outside the SQL gate; no read-only or error handling lives here.
/// </summary>
internal static class TsqlCatalogScan
{
    private const int CommandTimeoutSeconds = 180;

    public static async Task<IReadOnlyList<TableMetadataDto>> ExtractAsync(
        DbConnection connection,
        string columnsQuery,
        string foreignKeysQuery,
        string indexesQuery,
        Func<IEnumerable<dynamic>, IReadOnlyList<IndexMetadataDto>> mapIndexes,
        CancellationToken cancellationToken)
    {
        await connection.OpenAsync(cancellationToken);

        var columnsData = (await connection.QueryAsync(new CommandDefinition(columnsQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))).AsList();
        var foreignKeys = (await connection.QueryAsync(new CommandDefinition(foreignKeysQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))).AsList();
        var indexesData = (await connection.QueryAsync(new CommandDefinition(indexesQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))).AsList();

        return Assemble(columnsData, foreignKeys, indexesData, mapIndexes);
    }

    /// <summary>
    /// Builds the table list from the rows the catalog queries returned. Pure: no connection, so a fixture can pin it.
    /// </summary>
    public static IReadOnlyList<TableMetadataDto> Assemble(
        IEnumerable<dynamic> columnsData,
        IEnumerable<dynamic> foreignKeys,
        IEnumerable<dynamic> indexesData,
        Func<IEnumerable<dynamic>, IReadOnlyList<IndexMetadataDto>> mapIndexes)
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
                        IsNullable: (bool)c.is_nullable,
                        IsPrimaryKey: (int)c.is_primary_key == 1,
                        IsForeignKey: hasFk,
                        OrdinalPosition: (int)c.ordinal_position,
                        ForeignKeyTable: fkInfo?.TargetTable,
                        ForeignKeyColumn: fkInfo?.TargetColumn,
                        DefaultValue: null,
                        // sys.columns.max_length is boxed as short, so `as int?` is always null — convert explicitly.
                        // Note: for nvarchar/nchar this value is in bytes (2x the character length).
                        MaxLength: c.max_length == null ? (int?)null : Convert.ToInt32(c.max_length),
                        Description: null,
                        SampleValues: null,
                        ForeignKeySchema: fkInfo?.TargetSchema,
                        ForeignKeyConstraintName: fkInfo?.ConstraintName
                    )
                    {
                        ForeignKeys = foreignKeyList
                    };
                }).ToList();

                var tableIndexRows = indexesData
                    .Where(i => (string)i.table_schema == g.Key.schema && (string)i.table_name == g.Key.table)
                    .ToList();

                return new TableMetadataDto(
                    SchemaName: g.Key.schema,
                    TableName: g.Key.table,
                    Columns: columns,
                    Indexes: mapIndexes(tableIndexRows).ToList(),
                    Description: null
                );
            })
            .ToList();

        return tables;
    }
}
