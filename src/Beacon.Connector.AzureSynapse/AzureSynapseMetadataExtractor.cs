using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Providers;

namespace Beacon.Connector.AzureSynapse;

public class AzureSynapseMetadataExtractor : IDatabaseMetadataExtractor
{
    public DatabaseEngineType SupportedEngineType => DatabaseEngineType.AzureSynapse;

    private const string ColumnsQuery = @"
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

    private const string ForeignKeysQuery = @"
            SELECT
                s.name AS table_schema,
                t.name AS table_name,
                c.name AS column_name,
                rs.name AS foreign_schema_name,
                rt.name AS foreign_table_name,
                rc.name AS foreign_column_name,
                fkc.name AS constraint_name
            FROM sys.foreign_key_columns fk
            JOIN sys.tables t ON fk.parent_object_id = t.object_id
            JOIN sys.schemas s ON t.schema_id = s.schema_id
            JOIN sys.columns c ON fk.parent_object_id = c.object_id AND fk.parent_column_id = c.column_id
            JOIN sys.tables rt ON fk.referenced_object_id = rt.object_id
            JOIN sys.schemas rs ON rt.schema_id = rs.schema_id
            JOIN sys.columns rc ON fk.referenced_object_id = rc.object_id AND fk.referenced_column_id = rc.column_id
            JOIN sys.foreign_keys fkc ON fk.constraint_object_id = fkc.object_id";

    private const string IndexesQuery = @"
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

    public async Task<IReadOnlyList<TableMetadataDto>> ExtractMetadataAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = DbConnectionFactory.CreateConnection(DatabaseEngineType.AzureSynapse, connectionString);

        return await TsqlCatalogScan.ExtractAsync(connection, ColumnsQuery, ForeignKeysQuery, IndexesQuery, MapIndexRows, cancellationToken);
    }

    /// <summary>
    /// Builds the table list from the rows the catalog queries returned. Pure: no connection, so a fixture can pin it.
    /// </summary>
    internal static IReadOnlyList<TableMetadataDto> Assemble(IEnumerable<dynamic> columnsData, IEnumerable<dynamic> foreignKeys, IEnumerable<dynamic> indexesData)
        => TsqlCatalogScan.Assemble(columnsData, foreignKeys, indexesData, MapIndexRows);

    private static IReadOnlyList<IndexMetadataDto> MapIndexRows(IEnumerable<dynamic> indexRows)
        => indexRows
            .GroupBy(x => new { name = (string)x.index_name, is_unique = (bool)x.is_unique, is_pk = (bool)x.is_primary_key })
            .Select(y =>
                new IndexMetadataDto(
                    IndexName: y.Key.name,
                    IsUnique: y.Key.is_unique,
                    IsPrimaryKey: y.Key.is_pk,
                    Columns: y.OrderBy(z => (int)z.key_ordinal).Select(z => (string)z.column_name).ToArray()))
            .ToList();
}
