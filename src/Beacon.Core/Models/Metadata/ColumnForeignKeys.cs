using System.Text.Json;

namespace Beacon.Core.Models.Metadata;

/// <summary>
/// The foreign keys of one column (<see cref="ColumnMetadataDto.ForeignKeys"/>): their deterministic order, the fallback to the
/// single-target fields, and the stored form.
/// </summary>
public static class ColumnForeignKeys
{
    /// <summary>
    /// <paramref name="references"/> by constraint name (ordinal; a reference without a name last), then by target schema,
    /// table and column, so the first one, which fills the single-target fields, is the same on every read whatever order the
    /// catalog returned the rows in.
    /// </summary>
    public static IReadOnlyList<ColumnForeignKeyReference> InConstraintOrder(IEnumerable<ColumnForeignKeyReference> references)
    {
        return references
            .OrderBy(x => x.ConstraintName == null)
            .ThenBy(x => x.ConstraintName, StringComparer.Ordinal)
            .ThenBy(x => x.TargetSchema, StringComparer.Ordinal)
            .ThenBy(x => x.TargetTable, StringComparer.Ordinal)
            .ThenBy(x => x.TargetColumn, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The foreign keys of <paramref name="column"/>: its <see cref="ColumnMetadataDto.ForeignKeys"/> when supplied, else the one
    /// its single-target fields name, else none.
    /// </summary>
    public static IReadOnlyList<ColumnForeignKeyReference> Of(ColumnMetadataDto column)
    {
        if (column.ForeignKeys is { Count: > 0 })
        {
            return column.ForeignKeys;
        }

        if (string.IsNullOrEmpty(column.ForeignKeyTable) || string.IsNullOrEmpty(column.ForeignKeyColumn))
        {
            return [];
        }

        return [new ColumnForeignKeyReference(column.ForeignKeyConstraintName, column.ForeignKeySchema, column.ForeignKeyTable, column.ForeignKeyColumn)];
    }

    /// <summary>
    /// The stored form of <paramref name="references"/>: JSON when the column takes part in more than one foreign key, null
    /// otherwise (the single-target columns then hold the only one).
    /// </summary>
    internal static string? Serialize(IReadOnlyList<ColumnForeignKeyReference>? references)
    {
        return references is { Count: > 1 }
            ? JsonSerializer.Serialize(references)
            : null;
    }

    /// <summary>
    /// The references <see cref="Serialize"/> stored, or null for none or for a value that is not that JSON (the single-target
    /// columns are then read alone, as for a row stored before the list was kept).
    /// </summary>
    internal static IReadOnlyList<ColumnForeignKeyReference>? Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var references = JsonSerializer.Deserialize<List<ColumnForeignKeyReference>>(json);
            return references is { Count: > 0 } ? references : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
