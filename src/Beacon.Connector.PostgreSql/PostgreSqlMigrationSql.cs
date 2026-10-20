using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Connector.PostgreSql;

/// <summary>
/// The SQL text the PostgreSQL migration writer sends. Every name is a plain identifier already validated by Core and is
/// double-quoted here (§1.10), so it matches exactly as entered; values never appear in the text — they travel in the
/// binary COPY payload.
/// </summary>
internal static class PostgreSqlMigrationSql
{
    private static readonly string Dialect = DatabaseEngineType.PostgreSQL.ToString();

    /// <summary>The destination table, schema-qualified when a schema is named.</summary>
    public static string Table(string? schema, string table)
    {
        return schema == null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";
    }

    public static string Truncate(string? schema, string table)
    {
        return $"TRUNCATE TABLE {Table(schema, table)}";
    }

    /// <summary>A binary COPY of <paramref name="columns"/> into <paramref name="tableSql"/> (an already quoted table).</summary>
    public static string CopyInto(string tableSql, IReadOnlyList<string> columns)
    {
        return $"COPY {tableSql} ({ColumnList(columns)}) FROM STDIN (FORMAT BINARY)";
    }

    /// <summary>A fresh staging table name: unique per upsert chunk, lower case, so quoting does not change it.</summary>
    public static string NewStageName()
    {
        return $"beacon_stage_{Guid.NewGuid():N}";
    }

    /// <summary>
    /// An empty temporary table with the destination's <paramref name="columns"/> and their types, and nothing else: no
    /// constraints and no defaults, so a NOT NULL column the rows leave out does not fail the copy and no sequence value is
    /// spent. Dropped after its merge, and when the transaction ends at the latest.
    /// </summary>
    public static string CreateStage(string stageName, string? schema, string table, IReadOnlyList<string> columns)
    {
        return $"CREATE TEMP TABLE {Quote(stageName)} ON COMMIT DROP AS SELECT {ColumnList(columns)} FROM {Table(schema, table)} WITH NO DATA";
    }

    public static string DropStage(string stageName)
    {
        return $"DROP TABLE {Quote(stageName)}";
    }

    /// <summary>
    /// Moves the staged rows into the destination: a row whose key exists updates every non-key column; on a table whose
    /// columns are all key columns there is nothing to update, so an existing row is left as it is.
    /// </summary>
    public static string MergeFromStage(string stageName, string? schema, string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns)
    {
        var columnList = ColumnList(columns);
        var nonKeyColumns = columns
            .Where(x => !keyColumns.Contains(x, StringComparer.Ordinal))
            .ToList();
        var onConflict = nonKeyColumns.Count == 0
            ? "DO NOTHING"
            : "DO UPDATE SET " + string.Join(", ", nonKeyColumns.Select(x => $"{Quote(x)} = EXCLUDED.{Quote(x)}"));

        return $"INSERT INTO {Table(schema, table)} ({columnList}) SELECT {columnList} FROM {Quote(stageName)} "
            + $"ON CONFLICT ({ColumnList(keyColumns)}) {onConflict}";
    }

    private static string ColumnList(IEnumerable<string> columns)
    {
        return string.Join(", ", columns.Select(Quote));
    }

    private static string Quote(string identifier)
    {
        return SqlIdentifierGuard.Quote(SqlIdentifierGuard.Validate(identifier), Dialect);
    }
}
