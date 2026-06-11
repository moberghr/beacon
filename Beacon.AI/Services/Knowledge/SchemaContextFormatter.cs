using System.Text;
using System.Text.Json;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Column shape projected from metadata for LLM schema-context rendering.
/// <paramref name="SampleValuesJson"/> stays as the raw stored JSON because the record is
/// constructed inside EF projections where deserialization cannot be translated.
/// </summary>
internal record SchemaColumn(
    string ColumnName, string DataType, bool IsPrimaryKey, bool IsNullable,
    string? ForeignKeyTable, string? ForeignKeyColumn, string? Description,
    int? MaxLength = null, string? SampleValuesJson = null);

/// <summary>
/// Renders schema context for LLM grounding in an M-Schema-style structured format:
/// per-column tuples (name: type, flags, description, Examples: [..]) plus an explicit
/// Foreign Keys section per table.
/// </summary>
internal static class SchemaContextFormatter
{
    public static void AppendTableWithFullColumns(
        StringBuilder sb, string schemaName, string tableName, string? description,
        IEnumerable<SchemaColumn> columns, bool isApi)
    {
        var columnList = columns.ToList();

        sb.AppendLine($"### {(isApi ? tableName : $"{schemaName}.{tableName}")}");
        if (description != null)
        {
            sb.AppendLine($"  {description}");
        }

        sb.AppendLine("  Columns:");
        foreach (var col in columnList)
        {
            sb.Append($"    - ({col.ColumnName}: {FormatDataType(col)}");
            if (col.IsPrimaryKey)
            {
                sb.Append(", PK");
            }
            if (!col.IsNullable)
            {
                sb.Append(", NOT NULL");
            }
            if (col.Description != null)
            {
                sb.Append($", {col.Description}");
            }

            var examples = DeserializeSampleValues(col.SampleValuesJson);
            if (examples is { Count: > 0 })
            {
                sb.Append($", Examples: [{string.Join(", ", examples)}]");
            }

            sb.AppendLine(")");
        }

        var foreignKeys = columnList
            .Where(x => x.ForeignKeyTable != null)
            .ToList();
        if (foreignKeys.Count > 0)
        {
            sb.AppendLine("  Foreign Keys:");
            foreach (var fk in foreignKeys)
            {
                sb.AppendLine($"    - {fk.ColumnName} → {fk.ForeignKeyTable}.{fk.ForeignKeyColumn}");
            }
        }

        sb.AppendLine();
    }

    public static void AppendTableCompact(
        StringBuilder sb, string schemaName, string tableName, string? description,
        IEnumerable<SchemaColumn> columns, bool isApi)
    {
        var columnList = columns.ToList();
        var pks = columnList
            .Where(x => x.IsPrimaryKey)
            .Select(x => x.ColumnName)
            .ToList();
        var pkStr = pks.Count > 0 ? $"PK: {string.Join(", ", pks)}" : "no PK";
        var label = isApi ? tableName : $"{schemaName}.{tableName}";
        sb.Append($"  - {label} ({pkStr})");
        if (description != null)
        {
            sb.Append($" -- {description}");
        }
        sb.AppendLine();

        // Show all column names so LLM never has to guess
        var colNames = columnList
            .Select(x => x.ColumnName)
            .ToList();
        if (colNames.Count > 0)
        {
            sb.AppendLine($"    Columns: {string.Join(", ", colNames)}");
        }
    }

    private static string FormatDataType(SchemaColumn col)
    {
        if (col.MaxLength is > 0 && !col.DataType.Contains('('))
        {
            return $"{col.DataType}({col.MaxLength})";
        }

        return col.DataType;
    }

    private static IReadOnlyList<string>? DeserializeSampleValues(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json);
        }
        catch
        {
            // Invalid JSON — render without examples
            return null;
        }
    }
}
