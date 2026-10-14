using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Metadata;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// The table and column descriptions a person curated on a data source (by applying a documentation patch), as they are
/// stored now. A metadata refresh keeps them over the descriptions it extracts from the database catalog, so a refresh
/// never wipes them; every other description follows the catalog.
/// </summary>
internal sealed class CuratedDescriptions
{
    private readonly Dictionary<string, string> _tables;
    private readonly Dictionary<string, string> _columns;

    private CuratedDescriptions(Dictionary<string, string> tables, Dictionary<string, string> columns)
    {
        _tables = tables;
        _columns = columns;
    }

    public int Count => _tables.Count + _columns.Count;

    /// <summary>
    /// The curated descriptions among <paramref name="stored"/> (with their columns loaded): those an applied documentation
    /// patch targets. The patch's target identifier is <c>schema.table</c> or <c>schema.table.column</c>.
    /// </summary>
    public static async Task<CuratedDescriptions> LoadAsync(
        BeaconContext context,
        int dataSourceId,
        IReadOnlyList<DatabaseMetadata> stored,
        CancellationToken cancellationToken)
    {
        McpDocPatchTarget[] descriptionTargets = [McpDocPatchTarget.TableDescription, McpDocPatchTarget.ColumnDescription];
        var targets = await context.McpDocumentationPatches
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.Status == McpDocPatchStatus.Applied)
            .Where(x => descriptionTargets.Contains(x.TargetType))
            .Select(x =>
                new
                {
                    x.TargetType,
                    x.TargetIdentifier
                })
            .ToListAsync(cancellationToken);

        var tableTargets = targets
            .Where(x => x.TargetType == McpDocPatchTarget.TableDescription)
            .Select(x => x.TargetIdentifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var columnTargets = targets
            .Where(x => x.TargetType == McpDocPatchTarget.ColumnDescription)
            .Select(x => x.TargetIdentifier)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var tables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in stored)
        {
            var tableKey = TableKey(table.SchemaName, table.TableName);
            if (table.TableDescription != null && tableTargets.Contains(tableKey))
            {
                tables[tableKey] = table.TableDescription;
            }

            foreach (var column in table.Columns)
            {
                var columnKey = ColumnKey(table.SchemaName, table.TableName, column.ColumnName);
                if (column.Description != null && columnTargets.Contains(columnKey))
                {
                    columns[columnKey] = column.Description;
                }
            }
        }

        return new CuratedDescriptions(tables, columns);
    }

    /// <summary><paramref name="extracted"/>, with each curated description in place of the extracted one.</summary>
    public IReadOnlyList<TableMetadataDto> ApplyTo(IReadOnlyList<TableMetadataDto> extracted)
    {
        if (Count == 0)
        {
            return extracted;
        }

        return extracted
            .Select(x => x with
            {
                Description = _tables.GetValueOrDefault(TableKey(x.SchemaName, x.TableName)) ?? x.Description,
                Columns = x.Columns
                    .Select(y => y with
                    {
                        Description = _columns.GetValueOrDefault(ColumnKey(x.SchemaName, x.TableName, y.ColumnName)) ?? y.Description
                    })
                    .ToList()
            })
            .ToList();
    }

    private static string TableKey(string schemaName, string tableName) => $"{schemaName}.{tableName}";

    private static string ColumnKey(string schemaName, string tableName, string columnName) => $"{schemaName}.{tableName}.{columnName}";
}
