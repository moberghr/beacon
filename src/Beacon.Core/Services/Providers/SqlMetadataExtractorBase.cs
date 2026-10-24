using System.Data.Common;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;
using Dapper;

namespace Beacon.Core.Services.Providers;

/// <summary>
/// Shared catalog scan for the T-SQL engines (SQL Server, Synapse): three fixed catalog statements, then a pure
/// row-to-DTO assembly. Lives in Core because both connectors reference Core and never each other (§2.4).
/// The statements are fixed text that runs outside the SQL gate; this base holds no read-only or error handling.
/// </summary>
public abstract class SqlMetadataExtractorBase : IDatabaseMetadataExtractor
{
    private const int CommandTimeoutSeconds = 180;

    public abstract DatabaseEngineType SupportedEngineType { get; }

    /// <summary>Fixed catalog statement: one row per column (table_schema, table_name, column_name, data_type, is_nullable, max_length, ordinal_position, is_primary_key).</summary>
    protected abstract string ColumnsQuery { get; }

    /// <summary>Fixed catalog statement: one row per foreign key column.</summary>
    protected abstract string ForeignKeysQuery { get; }

    /// <summary>Fixed catalog statement: the rows <see cref="MapIndexes"/> turns into one table's indexes.</summary>
    protected abstract string IndexesQuery { get; }

    protected abstract DbConnection CreateConnection(string connectionString);

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var columnsData = (await connection.QueryAsync(new CommandDefinition(ColumnsQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))).AsList();
        var foreignKeys = (await connection.QueryAsync(new CommandDefinition(ForeignKeysQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))).AsList();
        var indexesData = (await connection.QueryAsync(new CommandDefinition(IndexesQuery, commandTimeout: CommandTimeoutSeconds, cancellationToken: cancellationToken))).AsList();

        return Assemble(columnsData, foreignKeys, indexesData, MapIndexes);
    }

    /// <summary>Turns the index rows of one table (those of <see cref="IndexesQuery"/> with its schema and name) into its indexes.</summary>
    protected abstract IReadOnlyList<IndexMetadataDto> MapIndexes(IEnumerable<dynamic> tableIndexRows);

    /// <summary>
    /// Builds the table list from the rows the catalog queries returned. Pure: no connection, so a fixture can pin it.
    /// </summary>
    protected static IReadOnlyList<TableMetadataDto> Assemble(
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
