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
        var fkLookup = foreignKeys
            .GroupBy(fk => $"{fk.table_schema}.{fk.table_name}.{fk.column_name}")
            .ToDictionary(
                g => g.Key,
                g => (
                    TableName: (string)g.First().foreign_table_name,
                    ColumnName: (string)g.First().foreign_column_name,
                    SchemaName: (string?)g.First().foreign_schema_name,
                    ConstraintName: (string?)g.First().constraint_name)
            );

        // One pass over the index rows instead of a scan of all of them per table.
        var indexesByTable = indexesData.ToLookup(i => ((string)i.table_schema, (string)i.table_name));

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
                        Description: null,
                        SampleValues: null,
                        ForeignKeySchema: hasFk ? fkInfo.SchemaName : null,
                        ForeignKeyConstraintName: hasFk ? fkInfo.ConstraintName : null
                    );
                }).ToList();

                return new TableMetadataDto(
                    SchemaName: g.Key.schema,
                    TableName: g.Key.table,
                    Columns: columns,
                    Indexes: mapIndexes(indexesByTable[(g.Key.schema, g.Key.table)]).ToList(),
                    Description: null
                );
            })
            .ToList();

        return tables;
    }
}
