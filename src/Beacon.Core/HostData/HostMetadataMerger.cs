using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Models.Metadata;

namespace Beacon.Core.HostData;

/// <summary>What one merge changed. The caller adds <see cref="AddedTables"/> and removes the dropped rows.</summary>
internal sealed record HostMetadataMergeResult(
    List<DatabaseMetadata> AddedTables,
    List<ColumnMetadata> RemovedColumns,
    List<IndexMetadata> RemovedIndexes,
    int UpdatedTables,
    int ArchivedTables);

/// <summary>
/// Merges the exposed host model into a data source's persisted metadata in place, so row identities (and any
/// description a user typed where the model has none) survive a model change. Pure — operates on the loaded
/// entities only; the synchronizer owns the unit of work.
/// </summary>
internal static class HostMetadataMerger
{
    public static HostMetadataMergeResult Merge(
        DataSource dataSource,
        List<DatabaseMetadata> existing,
        IReadOnlyList<TableMetadataDto> tables,
        DateTime now)
    {
        var added = new List<DatabaseMetadata>();
        var removedColumns = new List<ColumnMetadata>();
        var removedIndexes = new List<IndexMetadata>();
        var updated = 0;
        var matched = new HashSet<DatabaseMetadata>(ReferenceEqualityComparer.Instance);

        foreach (var table in tables)
        {
            var row = existing
                .Where(x => x.SchemaName.Equals(table.SchemaName, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.TableName.Equals(table.TableName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();

            if (row == null)
            {
                added.Add(CreateTable(dataSource, table, now));
                continue;
            }

            matched.Add(row);
            updated++;
            row.Unarchive();
            row.SchemaName = table.SchemaName;
            row.TableName = table.TableName;
            row.TableDescription = table.Description ?? row.TableDescription;
            row.LastRefreshed = now;

            MergeColumns(row, table.Columns, removedColumns);

            removedIndexes.AddRange(row.Indexes);
            row.Indexes.Clear();
            foreach (var index in table.Indexes)
            {
                row.Indexes.Add(ToIndex(index));
            }
        }

        var archived = 0;
        foreach (var row in existing)
        {
            if (matched.Contains(row) || row.ArchivedTime != null)
            {
                continue;
            }

            // No longer exposed (removed from the model or from the allow-list): archived, never served again.
            row.Archive();
            archived++;
        }

        return new HostMetadataMergeResult(added, removedColumns, removedIndexes, updated, archived);
    }

    private static void MergeColumns(DatabaseMetadata row, IReadOnlyList<ColumnMetadataDto> columns, List<ColumnMetadata> removed)
    {
        var byName = row.Columns.ToDictionary(x => x.ColumnName, StringComparer.OrdinalIgnoreCase);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var column in columns)
        {
            kept.Add(column.ColumnName);
            if (!byName.TryGetValue(column.ColumnName, out var existing))
            {
                row.Columns.Add(ToColumn(column));
                continue;
            }

            existing.ColumnName = column.ColumnName;
            existing.DataType = column.DataType;
            existing.IsNullable = column.IsNullable;
            existing.IsPrimaryKey = column.IsPrimaryKey;
            existing.IsForeignKey = column.IsForeignKey;
            existing.OrdinalPosition = column.OrdinalPosition;
            existing.ForeignKeyTable = column.ForeignKeyTable;
            existing.ForeignKeyColumn = column.ForeignKeyColumn;
            existing.ForeignKeySchema = column.ForeignKeySchema;
            existing.ForeignKeyConstraintName = column.ForeignKeyConstraintName;
            existing.MaxLength = column.MaxLength;
            existing.Description = column.Description ?? existing.Description;
        }

        // Excluded or dropped columns disappear from metadata entirely (ColumnMetadata has no soft delete).
        var dropped = row.Columns
            .Where(x => !kept.Contains(x.ColumnName))
            .ToList();

        foreach (var column in dropped)
        {
            row.Columns.Remove(column);
            removed.Add(column);
        }
    }

    private static DatabaseMetadata CreateTable(DataSource dataSource, TableMetadataDto table, DateTime now)
    {
        return new DatabaseMetadata
        {
            DataSourceId = dataSource.Id,
            DataSource = dataSource,
            SchemaName = table.SchemaName,
            TableName = table.TableName,
            TableDescription = table.Description,
            LastRefreshed = now,
            Columns = table.Columns
                .Select(ToColumn)
                .ToList(),
            Indexes = table.Indexes
                .Select(ToIndex)
                .ToList()
        };
    }

    private static ColumnMetadata ToColumn(ColumnMetadataDto column)
    {
        return new ColumnMetadata
        {
            ColumnName = column.ColumnName,
            DataType = column.DataType,
            IsNullable = column.IsNullable,
            IsPrimaryKey = column.IsPrimaryKey,
            IsForeignKey = column.IsForeignKey,
            OrdinalPosition = column.OrdinalPosition,
            ForeignKeyTable = column.ForeignKeyTable,
            ForeignKeyColumn = column.ForeignKeyColumn,
            ForeignKeySchema = column.ForeignKeySchema,
            ForeignKeyConstraintName = column.ForeignKeyConstraintName,
            MaxLength = column.MaxLength,
            Description = column.Description
        };
    }

    private static IndexMetadata ToIndex(IndexMetadataDto index)
    {
        return new IndexMetadata
        {
            IndexName = index.IndexName,
            IsUnique = index.IsUnique,
            IsPrimaryKey = index.IsPrimaryKey,
            Columns = index.Columns
        };
    }
}
