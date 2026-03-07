using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities.Projects;
using Semantico.Core.Data.Enums;

namespace Semantico.AI.Services.SchemaChangeDetection;

/// <summary>
/// Detects schema changes by comparing snapshots of database metadata over time.
/// </summary>
internal sealed class SchemaChangeDetectionService(
    IDbContextFactory<SemanticoContext> contextFactory,
    ILogger<SchemaChangeDetectionService> logger) : ISchemaChangeDetectionService
{
    private static readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = false };

    /// <summary>
    /// Captures the current database metadata for the given data source and saves it as a SchemaSnapshot.
    /// </summary>
    public async Task TakeSnapshotAsync(int dataSourceId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var tables = await context.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .Select(m => new SnapshotTableDto
            {
                SchemaName = m.SchemaName,
                TableName = m.TableName,
                TableDescription = m.TableDescription,
                Columns = m.Columns
                    .OrderBy(c => c.OrdinalPosition)
                    .Select(c => new SnapshotColumnDto
                    {
                        ColumnName = c.ColumnName,
                        DataType = c.DataType,
                        IsNullable = c.IsNullable,
                        IsPrimaryKey = c.IsPrimaryKey,
                        DefaultValue = c.DefaultValue,
                        ForeignKeyTable = c.ForeignKeyTable,
                        ForeignKeyColumn = c.ForeignKeyColumn
                    })
                    .ToList()
            })
            .OrderBy(t => t.SchemaName)
            .ThenBy(t => t.TableName)
            .ToListAsync(ct);

        var schemaJson = JsonSerializer.Serialize(tables, _jsonOptions);

        var snapshot = new SchemaSnapshot
        {
            DataSourceId = dataSourceId,
            CapturedAt = DateTime.UtcNow,
            SchemaJson = schemaJson
        };

        context.SchemaSnapshots.Add(snapshot);
        await context.SaveChangesAsync(ct);

        logger.LogInformation(
            "Schema snapshot taken for DataSource {DataSourceId}: {TableCount} tables captured",
            dataSourceId, tables.Count);
    }

    /// <summary>
    /// Compares the two most recent schema snapshots for the given data source and records any detected changes.
    /// Returns the count of changes found.
    /// </summary>
    public async Task<int> DetectChangesAsync(int dataSourceId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);

        var snapshots = await context.SchemaSnapshots
            .Where(s => s.DataSourceId == dataSourceId)
            .OrderByDescending(s => s.CapturedAt)
            .Take(2)
            .ToListAsync(ct);

        if (snapshots.Count < 2)
        {
            logger.LogInformation(
                "Not enough snapshots to detect changes for DataSource {DataSourceId} (found {Count})",
                dataSourceId, snapshots.Count);
            return 0;
        }

        // snapshots[0] is newer, snapshots[1] is older
        var current = DeserializeSnapshot(snapshots[0].SchemaJson);
        var previous = DeserializeSnapshot(snapshots[1].SchemaJson);

        var changes = new List<SchemaChange>();
        var detectedAt = DateTime.UtcNow;

        var currentByKey = current.ToDictionary(t => $"{t.SchemaName}.{t.TableName}");
        var previousByKey = previous.ToDictionary(t => $"{t.SchemaName}.{t.TableName}");

        // Detect added tables
        foreach (var key in currentByKey.Keys.Except(previousByKey.Keys))
        {
            var table = currentByKey[key];
            changes.Add(new SchemaChange
            {
                DataSourceId = dataSourceId,
                DetectedAt = detectedAt,
                ChangeType = SchemaChangeType.TableAdded,
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                Description = $"Table '{table.SchemaName}.{table.TableName}' was added"
            });
        }

        // Detect dropped tables
        foreach (var key in previousByKey.Keys.Except(currentByKey.Keys))
        {
            var table = previousByKey[key];
            changes.Add(new SchemaChange
            {
                DataSourceId = dataSourceId,
                DetectedAt = detectedAt,
                ChangeType = SchemaChangeType.TableDropped,
                SchemaName = table.SchemaName,
                TableName = table.TableName,
                Description = $"Table '{table.SchemaName}.{table.TableName}' was dropped"
            });
        }

        // Detect column-level changes in tables that exist in both snapshots
        foreach (var key in currentByKey.Keys.Intersect(previousByKey.Keys))
        {
            var currentTable = currentByKey[key];
            var previousTable = previousByKey[key];

            var currentColumns = currentTable.Columns.ToDictionary(c => c.ColumnName);
            var previousColumns = previousTable.Columns.ToDictionary(c => c.ColumnName);

            // Added columns
            foreach (var colName in currentColumns.Keys.Except(previousColumns.Keys))
            {
                var col = currentColumns[colName];
                changes.Add(new SchemaChange
                {
                    DataSourceId = dataSourceId,
                    DetectedAt = detectedAt,
                    ChangeType = SchemaChangeType.ColumnAdded,
                    SchemaName = currentTable.SchemaName,
                    TableName = currentTable.TableName,
                    ColumnName = colName,
                    NewValue = col.DataType,
                    Description = $"Column '{colName}' ({col.DataType}) was added to '{key}'"
                });
            }

            // Dropped columns
            foreach (var colName in previousColumns.Keys.Except(currentColumns.Keys))
            {
                var col = previousColumns[colName];
                changes.Add(new SchemaChange
                {
                    DataSourceId = dataSourceId,
                    DetectedAt = detectedAt,
                    ChangeType = SchemaChangeType.ColumnDropped,
                    SchemaName = currentTable.SchemaName,
                    TableName = currentTable.TableName,
                    ColumnName = colName,
                    OldValue = col.DataType,
                    Description = $"Column '{colName}' ({col.DataType}) was dropped from '{key}'"
                });
            }

            // Type-changed columns
            foreach (var colName in currentColumns.Keys.Intersect(previousColumns.Keys))
            {
                var currentCol = currentColumns[colName];
                var previousCol = previousColumns[colName];

                if (!string.Equals(currentCol.DataType, previousCol.DataType, StringComparison.OrdinalIgnoreCase))
                {
                    changes.Add(new SchemaChange
                    {
                        DataSourceId = dataSourceId,
                        DetectedAt = detectedAt,
                        ChangeType = SchemaChangeType.ColumnTypeChanged,
                        SchemaName = currentTable.SchemaName,
                        TableName = currentTable.TableName,
                        ColumnName = colName,
                        OldValue = previousCol.DataType,
                        NewValue = currentCol.DataType,
                        Description = $"Column '{colName}' in '{key}' changed type from '{previousCol.DataType}' to '{currentCol.DataType}'"
                    });
                }
            }
        }

        if (changes.Count > 0)
        {
            context.SchemaChanges.AddRange(changes);
            await context.SaveChangesAsync(ct);
        }

        logger.LogInformation(
            "Schema change detection complete for DataSource {DataSourceId}: {ChangeCount} changes detected",
            dataSourceId, changes.Count);

        return changes.Count;
    }

    private static List<SnapshotTableDto> DeserializeSnapshot(string json)
        => JsonSerializer.Deserialize<List<SnapshotTableDto>>(json) ?? new List<SnapshotTableDto>();

    // DTO types used only for snapshot serialization
    private sealed class SnapshotTableDto
    {
        public string SchemaName { get; set; } = string.Empty;
        public string TableName { get; set; } = string.Empty;
        public string? TableDescription { get; set; }
        public List<SnapshotColumnDto> Columns { get; set; } = new();
    }

    private sealed class SnapshotColumnDto
    {
        public string ColumnName { get; set; } = string.Empty;
        public string DataType { get; set; } = string.Empty;
        public bool IsNullable { get; set; }
        public bool IsPrimaryKey { get; set; }
        public string? DefaultValue { get; set; }
        public string? ForeignKeyTable { get; set; }
        public string? ForeignKeyColumn { get; set; }
    }
}
