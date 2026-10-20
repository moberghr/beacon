using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Connector.SqlServer;

/// <summary>
/// The SQL text the SQL Server migration writer sends. Every name is a plain identifier already validated by Core and is
/// bracket-quoted here (§1.10); values never appear in the text — they travel in the bulk-copy payload, and the one lookup
/// that takes an argument binds it as a parameter.
/// </summary>
internal static class SqlServerMigrationSql
{
    /// <summary>The parameter <see cref="DestinationColumns"/> binds the quoted destination table to.</summary>
    public const string TableParameter = "@table";

    private static readonly string Dialect = DatabaseEngineType.MSSQL.ToString();

    /// <summary>The destination table, schema-qualified when a schema is named.</summary>
    public static string Table(string? schema, string table)
    {
        return schema == null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";
    }

    public static string Truncate(string? schema, string table)
    {
        return $"TRUNCATE TABLE {Table(schema, table)}";
    }

    /// <summary>
    /// The columns of the table named by <see cref="TableParameter"/> (its quoted, possibly qualified name), in table order,
    /// each with whether it is an identity column and whether its values can be compared. text, ntext, image and xml
    /// (system type ids 34, 35, 99, 241) and the CLR types — geography, geometry, hierarchyid and user-defined ones (240) —
    /// cannot take part in <c>EXCEPT</c>; hierarchyid could, but is treated like the rest.
    /// </summary>
    public static string DestinationColumns()
    {
        return "SELECT c.name, c.is_identity, CAST(CASE WHEN c.system_type_id IN (34, 35, 99, 240, 241) THEN 0 ELSE 1 END AS bit) AS is_comparable "
            + $"FROM sys.columns AS c WHERE c.object_id = OBJECT_ID({TableParameter}) ORDER BY c.column_id";
    }

    /// <summary>A fresh staging table name (without the temporary-table <c>#</c>): unique per upsert.</summary>
    public static string NewStageName()
    {
        return $"beacon_stage_{Guid.NewGuid():N}";
    }

    /// <summary>The session-local temporary table of <paramref name="stageName"/>.</summary>
    public static string StageTable(string stageName)
    {
        return SqlIdentifierGuard.Quote("#" + SqlIdentifierGuard.Validate(stageName, "stage table"), Dialect);
    }

    /// <summary>An empty temporary table with the destination's <paramref name="columns"/> and their types.</summary>
    public static string CreateStage(string stageName, string? schema, string table, IReadOnlyList<string> columns)
    {
        return $"SELECT TOP 0 {ColumnList(columns)} INTO {StageTable(stageName)} FROM {Table(schema, table)}";
    }

    /// <summary>
    /// Moves the staged rows into the destination: a row whose key exists is updated only when one of its updatable columns
    /// changed (compared null-safely with <c>EXCEPT</c>), a new key is inserted. When an updatable column cannot be compared
    /// (<paramref name="nonComparableColumns"/>) every matched row is updated instead. Key and identity columns are never
    /// updated; on a table where nothing is left to update an existing row stays as it is. When an inserted column is an
    /// identity column the statement runs with <c>IDENTITY_INSERT</c> on, so the given values are kept.
    /// </summary>
    public static string MergeFromStage(
        string stageName,
        string? schema,
        string table,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> identityColumns,
        IReadOnlyList<string> nonComparableColumns)
    {
        var destination = Table(schema, table);
        var updatable = columns
            .Where(x => !keyColumns.Contains(x, StringComparer.Ordinal))
            .Where(x => !identityColumns.Contains(x, StringComparer.Ordinal))
            .ToList();
        var on = string.Join(" AND ", keyColumns.Select(x => $"target.{Quote(x)} = source.{Quote(x)}"));
        var changedOnly = updatable.Any(x => nonComparableColumns.Contains(x, StringComparer.Ordinal))
            ? ""
            : $" AND EXISTS (SELECT {Prefixed("source", updatable)} EXCEPT SELECT {Prefixed("target", updatable)})";
        var whenMatched = updatable.Count == 0
            ? ""
            : $" WHEN MATCHED{changedOnly}"
                + $" THEN UPDATE SET {string.Join(", ", updatable.Select(x => $"target.{Quote(x)} = source.{Quote(x)}"))}";
        var merge = $"MERGE {destination} WITH (HOLDLOCK) AS target USING {StageTable(stageName)} AS source ON {on}{whenMatched}"
            + $" WHEN NOT MATCHED THEN INSERT ({ColumnList(columns)}) VALUES ({Prefixed("source", columns)});";

        return InsertsIdentity(columns, identityColumns)
            ? $"{IdentityInsert(schema, table, true)} {merge} {IdentityInsert(schema, table, false)}"
            : merge;
    }

    /// <summary>True when one of <paramref name="columns"/> is an identity column, so inserting it needs <c>IDENTITY_INSERT</c>.</summary>
    public static bool InsertsIdentity(IReadOnlyList<string> columns, IReadOnlyList<string> identityColumns)
    {
        return columns.Any(x => identityColumns.Contains(x, StringComparer.Ordinal));
    }

    public static string IdentityInsert(string? schema, string table, bool on)
    {
        return $"SET IDENTITY_INSERT {Table(schema, table)} {(on ? "ON" : "OFF")};";
    }

    public static string DropStage(string stageName)
    {
        return $"DROP TABLE IF EXISTS {StageTable(stageName)}";
    }

    private static string ColumnList(IEnumerable<string> columns)
    {
        return string.Join(", ", columns.Select(Quote));
    }

    private static string Prefixed(string alias, IEnumerable<string> columns)
    {
        return string.Join(", ", columns.Select(x => $"{alias}.{Quote(x)}"));
    }

    private static string Quote(string identifier)
    {
        return SqlIdentifierGuard.Quote(SqlIdentifierGuard.Validate(identifier), Dialect);
    }
}
