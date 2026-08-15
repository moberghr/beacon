using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.Core.Models.Metadata;

namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// Column shape projected from metadata for LLM schema-context rendering.
/// <paramref name="SampleValuesJson"/> stays as the raw stored JSON because the record is
/// constructed inside EF projections where deserialization cannot be translated.
/// </summary>
internal record SchemaColumn(
    string ColumnName, string DataType, bool IsPrimaryKey, bool IsNullable,
    string? ForeignKeyTable, string? ForeignKeyColumn, string? Description,
    int? MaxLength = null, string? SampleValuesJson = null,
    string? ForeignKeySchema = null);

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

    /// <summary>
    /// Renders join paths as explicit join chains, split into a verified block and an unverified one.
    /// Without this the model has to infer joins from foreign-key columns; with it the join is grounded
    /// fact for verified relationships and an openly-flagged guess for inferred ones.
    /// </summary>
    public static void AppendJoinPaths(StringBuilder sb, IReadOnlyList<SchemaJoinPath> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        var verified = paths
            .Where(x => x.IsFullyVerified)
            .ToList();
        var unverified = paths
            .Where(x => !x.IsFullyVerified)
            .ToList();

        if (verified.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Join Paths (verified)");
            foreach (var path in verified)
            {
                AppendJoinPath(sb, path, showConfidence: false);
            }
        }

        if (unverified.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Join Paths (UNVERIFIED — inferred from column naming, confirm before relying on these)");
            foreach (var path in unverified)
            {
                AppendJoinPath(sb, path, showConfidence: true);
            }
        }
    }

    /// <summary>
    /// States how much of the schema neighbourhood was described. A silently truncated set reads as
    /// "this is everything related", which is exactly the wrong signal to give the model.
    /// </summary>
    public static void AppendCoverage(StringBuilder sb, bool capped, int omittedTableCount, int detailedTableCount)
    {
        if (!capped)
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("## Coverage");
        sb.AppendLine($"  Detailed tables truncated at {detailedTableCount}: {omittedTableCount} related table(s) omitted. "
            + "Other tables are listed by name only — ask for more detail if the answer needs one of them.");
    }

    private static void AppendJoinPath(StringBuilder sb, SchemaJoinPath path, bool showConfidence)
    {
        var chain = new List<string> { path.FromQualifiedName };
        chain.AddRange(path.Steps.Select(x => x.ToQualifiedName));

        sb.AppendLine($"- {string.Join(" → ", chain)}");

        foreach (var step in path.Steps)
        {
            var line = $"    {step.FromQualifiedName}.{step.FromColumn} = {step.ToQualifiedName}.{step.ToColumn}";
            if (showConfidence && !step.IsVerified)
            {
                line += $"   (inferred, confidence {step.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})";
            }

            sb.AppendLine(line);
        }

        var junctions = path.Steps
            .Where(x => x.ToIsJunction)
            .Select(x => x.ToQualifiedName)
            .Distinct()
            .ToList();

        foreach (var junction in junctions)
        {
            sb.AppendLine($"    [link table: {junction}]");
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
