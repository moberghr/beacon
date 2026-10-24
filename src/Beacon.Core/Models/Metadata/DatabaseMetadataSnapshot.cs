using Beacon.Core.Data.Enums;

namespace Beacon.Core.Models.Metadata;

public record DatabaseMetadataSnapshot(
    int DataSourceId,
    DatabaseEngineType? DatabaseEngineType,
    IReadOnlyList<TableMetadataDto> Tables,
    DateTime RefreshedAt
)
{
    /// <summary>
    /// The table names, column names and foreign-key targets the refresh that read this snapshot left out because they carry
    /// a quote, bracket, semicolon or control character (logged by name); 0 for a snapshot read from storage.
    /// </summary>
    public int SkippedNameCount { get; init; }
}

public record TableMetadataDto(
    string SchemaName,
    string TableName,
    IReadOnlyList<ColumnMetadataDto> Columns,
    IReadOnlyList<IndexMetadataDto> Indexes,
    string? Description
);

/// <summary>
/// <paramref name="ForeignKeySchema"/> qualifies <paramref name="ForeignKeyTable"/>: without it a
/// consumer must match the target on bare table name, which resolves to the wrong table when the same
/// name exists in two schemas. <paramref name="ForeignKeyConstraintName"/> groups the column pairs of a
/// composite foreign key — the pairs must be joined together, and nothing else in this column-level
/// shape conveys that.
/// </summary>
public record ColumnMetadataDto(
    string ColumnName,
    string DataType,
    bool IsNullable,
    bool IsPrimaryKey,
    bool IsForeignKey,
    int OrdinalPosition,
    string? ForeignKeyTable,
    string? ForeignKeyColumn,
    string? DefaultValue,
    int? MaxLength,
    string? Description,
    IReadOnlyList<string>? SampleValues = null,
    string? ForeignKeySchema = null,
    string? ForeignKeyConstraintName = null,
    bool SampleValuesComplete = false
)
{
    /// <summary>
    /// Every foreign key this column takes part in, in constraint-name order (<see cref="ColumnForeignKeys.InConstraintOrder"/>).
    /// A column can take part in several, e.g. <c>tenant_id</c> referencing <c>tenants</c> on its own and <c>x</c> together
    /// with <c>x_id</c>. The single-target fields above carry the first of them. Null when only those fields are known (a
    /// connector or stored row that does not supply the list): read it through <see cref="ColumnForeignKeys.Of"/>.
    /// </summary>
    public IReadOnlyList<ColumnForeignKeyReference>? ForeignKeys { get; init; }
}

/// <summary>One foreign key a column takes part in: the constraint and the target column this column is paired with.</summary>
public record ColumnForeignKeyReference(
    string? ConstraintName,
    string? TargetSchema,
    string TargetTable,
    string TargetColumn
);

public record IndexMetadataDto(
    string IndexName,
    bool IsUnique,
    bool IsPrimaryKey,
    string[] Columns
);

/// <summary>
/// One foreign key reconstructed from the column-level metadata shape: the column pairs sharing a
/// constraint name, which must be joined together. A composite key yields one instance with several
/// <see cref="ColumnPairs"/>.
/// </summary>
public record ForeignKeyConstraintDto(
    string? ConstraintName,
    string? TargetSchema,
    string TargetTable,
    IReadOnlyList<ForeignKeyColumnPair> ColumnPairs
);

public record ForeignKeyColumnPair(string SourceColumn, string TargetColumn);

/// <summary>
/// Resolves foreign-key targets against a table set. Matching on bare table name silently picks the
/// wrong table when the same name exists in two schemas, so resolution is schema-qualified whenever the
/// extractor supplied a target schema, and refuses to guess when it did not and the name is ambiguous.
/// </summary>
public static class ForeignKeyTargetResolver
{
    /// <summary>
    /// Returns the table <paramref name="column"/> points at, or null when the column is not a foreign
    /// key, the target is absent from <paramref name="tables"/>, or the target name is ambiguous across
    /// schemas with no qualifying schema to disambiguate it.
    /// </summary>
    public static TableMetadataDto? Resolve(
        IReadOnlyList<TableMetadataDto> tables,
        string sourceSchema,
        ColumnMetadataDto column)
    {
        return Resolve(tables, sourceSchema, column.ForeignKeyTable, column.ForeignKeySchema);
    }

    /// <summary>
    /// Returns the table <paramref name="foreignKey"/> (one of a column's <see cref="ColumnForeignKeys.Of"/>) points at, under the
    /// same rules as the column overload.
    /// </summary>
    public static TableMetadataDto? Resolve(
        IReadOnlyList<TableMetadataDto> tables,
        string sourceSchema,
        ColumnForeignKeyReference foreignKey)
    {
        return Resolve(tables, sourceSchema, foreignKey.TargetTable, foreignKey.TargetSchema);
    }

    /// <summary>
    /// Groups a table's foreign-key columns into constraints. Columns sharing a constraint name form one
    /// composite key; columns with no constraint name (legacy rows, or a connector that does not expose
    /// it) are each treated as their own single-column key.
    /// </summary>
    public static IReadOnlyList<ForeignKeyConstraintDto> GroupForeignKeys(TableMetadataDto table)
    {
        var fkColumns = table.Columns
            .Where(x => !string.IsNullOrEmpty(x.ForeignKeyTable))
            .Where(x => !string.IsNullOrEmpty(x.ForeignKeyColumn))
            .OrderBy(x => x.OrdinalPosition)
            .ToList();

        var constraints = new List<ForeignKeyConstraintDto>();
        var grouped = fkColumns
            .Where(x => !string.IsNullOrEmpty(x.ForeignKeyConstraintName))
            .GroupBy(x => x.ForeignKeyConstraintName!, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var first = group.First();
            constraints.Add(new ForeignKeyConstraintDto(
                group.Key,
                first.ForeignKeySchema,
                first.ForeignKeyTable!,
                group.Select(x => new ForeignKeyColumnPair(x.ColumnName, x.ForeignKeyColumn!)).ToList()));
        }

        var ungrouped = fkColumns
            .Where(x => string.IsNullOrEmpty(x.ForeignKeyConstraintName))
            .ToList();

        foreach (var column in ungrouped)
        {
            constraints.Add(new ForeignKeyConstraintDto(
                null,
                column.ForeignKeySchema,
                column.ForeignKeyTable!,
                [new ForeignKeyColumnPair(column.ColumnName, column.ForeignKeyColumn!)]));
        }

        return constraints;
    }

    private static TableMetadataDto? Resolve(
        IReadOnlyList<TableMetadataDto> tables,
        string sourceSchema,
        string? targetTable,
        string? targetSchema)
    {
        if (string.IsNullOrEmpty(targetTable))
        {
            return null;
        }

        var nameMatches = tables
            .Where(x => x.TableName.Equals(targetTable, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (nameMatches.Count == 0)
        {
            return null;
        }

        // Extractor supplied the target schema — a qualified match is the only correct answer.
        if (!string.IsNullOrEmpty(targetSchema))
        {
            return nameMatches
                .Where(x => x.SchemaName.Equals(targetSchema, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
        }

        if (nameMatches.Count == 1)
        {
            return nameMatches[0];
        }

        // Unqualified and ambiguous: same-schema is the only defensible assumption. Cross-schema with
        // several candidates and no qualifier stays unresolved rather than picking whichever sorts first.
        return nameMatches
            .Where(x => x.SchemaName.Equals(sourceSchema, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
    }
}
