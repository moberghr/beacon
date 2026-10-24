using System.Globalization;
using System.Text;
using System.Text.Json;
using Beacon.Core.Data.Enums;
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
    string? ForeignKeySchema = null, bool SampleValuesComplete = false);

/// <summary>
/// How a prompt lists a table's columns. Each prompt keeps its own wording: a change to it changes what the model reads.
/// </summary>
internal enum SchemaColumnLayout
{
    /// <summary><c>    - (name: type(length), PK, NOT NULL, description, Examples: [..])</c>, for SQL generation.</summary>
    Tuple,

    /// <summary><c>  - name type PK NOT NULL FK→table.column — description</c>.</summary>
    Inline,

    /// <summary><c>  - name (type) [PK] FK→table.column</c>, primary and foreign key columns only.</summary>
    KeyColumns,

    /// <summary><c>  - name (type) — description</c>.</summary>
    Fields
}

/// <summary>
/// The shape of one table block in a prompt: how its description and columns are introduced, whether a foreign-key section,
/// a line naming every column, or a trailing blank line follow. With <c>OneLineText</c> the table description is rendered
/// on one line (<see cref="PromptData.OneLine"/>), so text from the database cannot start a heading, a table or a section
/// of its own; the <see cref="SchemaColumnLayout.Tuple"/> columns always render their descriptions and values that way.
/// </summary>
internal sealed record SchemaTableLayout(
    SchemaColumnLayout Columns,
    string DescriptionPrefix,
    string? ColumnsHeading,
    bool ForeignKeySection,
    bool ColumnNamesLine,
    bool BlankLineAfter,
    bool OneLineText = false)
{
    /// <summary>The ask schema context: M-Schema-style column tuples plus an explicit Foreign Keys section.</summary>
    public static SchemaTableLayout Ask { get; } = new(SchemaColumnLayout.Tuple, "  ", "  Columns:", ForeignKeySection: true, ColumnNamesLine: false, BlankLineAfter: true, OneLineText: true);

    /// <summary>The documentation data model prompt; the caller appends indexes and enum values, then the blank line.</summary>
    public static SchemaTableLayout DocumentationDataModel { get; } = new(SchemaColumnLayout.Inline, "Description: ", "Columns:", ForeignKeySection: false, ColumnNamesLine: false, BlankLineAfter: false);

    /// <summary>The documentation business domains prompt: key columns, then every column's name.</summary>
    public static SchemaTableLayout DocumentationDomains { get; } = new(SchemaColumnLayout.KeyColumns, "Description: ", ColumnsHeading: null, ForeignKeySection: false, ColumnNamesLine: true, BlankLineAfter: false);

    /// <summary>The documentation API prompt: an endpoint's response fields.</summary>
    public static SchemaTableLayout DocumentationApi { get; } = new(SchemaColumnLayout.Fields, "Description: ", "Response fields:", ForeignKeySection: false, ColumnNamesLine: false, BlankLineAfter: true);
}

/// <summary>
/// Renders tables and columns into prompt text, for the ask schema context and the documentation prompts. Each caller
/// picks its <see cref="SchemaTableLayout"/>; the formatter renders only what the columns carry (sample values only in the
/// <see cref="SchemaColumnLayout.Tuple"/> layout, and only when the caller passed them). The AI actor's schema context
/// (<c>AiActorPrompts.FormatSchemaContext</c>) keeps its own rendering: it is public and marks a foreign key by the stored
/// flag with a preformatted reference, not by the target table the layouts here key on.
/// </summary>
internal static class SchemaContextFormatter
{
    public static void AppendTableWithFullColumns(
        StringBuilder sb, string schemaName, string tableName, string? description,
        IEnumerable<SchemaColumn> columns, bool isApi) =>
        AppendTable(sb, isApi ? tableName : $"{schemaName}.{tableName}", description, columns, SchemaTableLayout.Ask);

    /// <summary>
    /// Appends one table block: <c>### label</c>, the description, the columns in the layout's form, and the sections the
    /// layout asks for.
    /// </summary>
    public static void AppendTable(
        StringBuilder sb, string label, string? description, IEnumerable<SchemaColumn> columns, SchemaTableLayout layout)
    {
        var columnList = columns.ToList();

        sb.AppendLine($"### {label}");
        if (description != null)
        {
            sb.AppendLine($"{layout.DescriptionPrefix}{(layout.OneLineText ? PromptData.OneLine(description) : description)}");
        }

        if (layout.ColumnsHeading != null)
        {
            sb.AppendLine(layout.ColumnsHeading);
        }

        foreach (var col in columnList)
        {
            AppendColumn(sb, col, layout.Columns);
        }

        if (layout.ColumnNamesLine)
        {
            sb.AppendLine($"  Columns: {string.Join(", ", columnList.Select(x => x.ColumnName))}");
        }

        if (layout.ForeignKeySection)
        {
            AppendForeignKeySection(sb, columnList);
        }

        if (layout.BlankLineAfter)
        {
            sb.AppendLine();
        }
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
            sb.Append($" -- {PromptData.OneLine(description)}");
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
    /// fact for verified relationships and an openly-flagged guess for unverified ones: an inferred edge with its
    /// confidence, an edge a user added as pending curator verification.
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
            sb.AppendLine("## Join Paths (UNVERIFIED — pending curator verification, confirm before relying on these)");
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
                // Only an inferred edge has a confidence of its own: a foreign-key or manual edge's 1.00 would read as
                // certainty it has not earned while unverified.
                line += step.Origin == SchemaRelationshipOrigin.Inferred
                    ? $"   (inferred from column naming, confidence {step.Confidence.ToString("0.00", CultureInfo.InvariantCulture)})"
                    : "   (added, pending curator verification)";
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

    private static void AppendColumn(StringBuilder sb, SchemaColumn col, SchemaColumnLayout layout)
    {
        switch (layout)
        {
            case SchemaColumnLayout.Tuple:
                AppendTupleColumn(sb, col);
                break;
            case SchemaColumnLayout.Inline:
                AppendInlineColumn(sb, col);
                break;
            case SchemaColumnLayout.KeyColumns:
                AppendKeyColumn(sb, col);
                break;
            case SchemaColumnLayout.Fields:
                AppendField(sb, col);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(layout), layout, null);
        }
    }

    private static void AppendInlineColumn(StringBuilder sb, SchemaColumn col)
    {
        sb.Append($"  - {col.ColumnName} {col.DataType}");
        if (col.IsPrimaryKey)
        {
            sb.Append(" PK");
        }
        if (!col.IsNullable)
        {
            sb.Append(" NOT NULL");
        }
        if (col.ForeignKeyTable != null)
        {
            sb.Append($" FK→{col.ForeignKeyTable}.{col.ForeignKeyColumn}");
        }
        if (col.Description != null)
        {
            sb.Append($" — {col.Description}");
        }

        sb.AppendLine();
    }

    private static void AppendKeyColumn(StringBuilder sb, SchemaColumn col)
    {
        if (!col.IsPrimaryKey && col.ForeignKeyTable == null)
        {
            return;
        }

        sb.Append($"  - {col.ColumnName} ({col.DataType})");
        if (col.IsPrimaryKey)
        {
            sb.Append(" [PK]");
        }
        if (col.ForeignKeyTable != null)
        {
            sb.Append($" FK→{col.ForeignKeyTable}.{col.ForeignKeyColumn}");
        }

        sb.AppendLine();
    }

    private static void AppendField(StringBuilder sb, SchemaColumn col)
    {
        sb.Append($"  - {col.ColumnName} ({col.DataType})");
        if (col.Description != null)
        {
            sb.Append($" — {col.Description}");
        }

        sb.AppendLine();
    }

    private static void AppendTupleColumn(StringBuilder sb, SchemaColumn col)
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
            sb.Append($", {PromptData.OneLine(col.Description)}");
        }

        var examples = DeserializeSampleValues(col.SampleValuesJson);
        if (examples is { Count: > 0 })
        {
            var values = string.Join(", ", examples.Select(PromptData.OneLine));
            sb.Append(col.SampleValuesComplete
                ? $", Values (all {examples.Count}): [{values}]"
                : $", Examples: [{values}]");
        }

        sb.AppendLine(")");
    }

    private static void AppendForeignKeySection(StringBuilder sb, List<SchemaColumn> columns)
    {
        var foreignKeys = columns
            .Where(x => x.ForeignKeyTable != null)
            .ToList();
        if (foreignKeys.Count == 0)
        {
            return;
        }

        sb.AppendLine("  Foreign Keys:");
        foreach (var fk in foreignKeys)
        {
            sb.AppendLine($"    - {fk.ColumnName} → {fk.ForeignKeyTable}.{fk.ForeignKeyColumn}");
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

    internal static IReadOnlyList<string>? DeserializeSampleValues(string? json)
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
