using System.Text;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Connector.MySql;

/// <summary>
/// The SQL text the MySQL migration writer sends. Every name is a plain identifier already validated by Core and is
/// backtick-quoted here (§1.10); values never appear in the text — each one is bound to its own <c>@p{n}</c> parameter.
/// </summary>
internal static class MySqlMigrationSql
{
    /// <summary>The most rows one INSERT command carries.</summary>
    public const int MaxRowsPerCommand = Beacon.Core.Constants.Migration.UpsertBatchSize;

    /// <summary>The most parameters one INSERT command binds; MySQL refuses a prepared statement with more than 65,535.</summary>
    public const int MaxParametersPerCommand = 60_000;

    /// <summary>
    /// The most estimated parameter payload, in bytes, one INSERT command carries (see <see cref="EstimatedSize"/>). The
    /// driver escapes each value into the statement it sends, which at most doubles it, so a command stays well under the
    /// 4 MB <c>max_allowed_packet</c> MySQL 5.7 defaults to, with room for the statement text around the values.
    /// </summary>
    public const int MaxPayloadBytesPerCommand = 1024 * 1024;

    /// <summary>
    /// The estimated payload of a value that is neither text nor bytes. The driver sends such values as text: a GUID takes 36
    /// characters, a decimal up to 31 digits with its sign and point, a date and time with microseconds 26.
    /// </summary>
    public const int FixedValueSize = 40;

    private static readonly string Dialect = DatabaseEngineType.MySQL.ToString();

    /// <summary>The destination table, schema-qualified when a schema is named.</summary>
    public static string Table(string? schema, string table)
    {
        return schema == null ? Quote(table) : $"{Quote(schema)}.{Quote(table)}";
    }

    /// <summary>
    /// Removes every row. <c>TRUNCATE</c> would commit the run's transaction implicitly on MySQL, so a later failure could
    /// no longer roll the clearing back; <c>DELETE</c> stays inside it.
    /// </summary>
    public static string Clear(string? schema, string table)
    {
        return $"DELETE FROM {Table(schema, table)}";
    }

    /// <summary>The parameter carrying the destination's schema in <see cref="ReferencingForeignKeyCount"/> (NULL: the connection's database).</summary>
    public const string SchemaParameter = "@schema";

    /// <summary>The parameter carrying the destination's table name in <see cref="ReferencingForeignKeyCount"/>.</summary>
    public const string TableParameter = "@table";

    /// <summary>
    /// Counts the foreign keys of other tables that reference the destination. <c>TRUNCATE</c> refuses such a table, while
    /// <see cref="Clear"/>'s <c>DELETE</c> would cascade into, or set NULL in, the referencing rows. A table's reference to
    /// itself is not counted: the DELETE removes every row it could reach, as <c>TRUNCATE</c> allows.
    /// </summary>
    public static string ReferencingForeignKeyCount()
    {
        return "SELECT COUNT(*) FROM information_schema.REFERENTIAL_CONSTRAINTS "
            + $"WHERE UNIQUE_CONSTRAINT_SCHEMA = COALESCE({SchemaParameter}, DATABASE()) "
            + $"AND REFERENCED_TABLE_NAME = {TableParameter} "
            + "AND NOT (CONSTRAINT_SCHEMA = UNIQUE_CONSTRAINT_SCHEMA AND TABLE_NAME = REFERENCED_TABLE_NAME)";
    }

    /// <summary>The name of the parameter that carries value <paramref name="index"/> of a command.</summary>
    public static string ParameterName(int index)
    {
        return $"@p{index}";
    }

    /// <summary>
    /// A multi-row INSERT of <paramref name="rowCount"/> rows of <paramref name="columns"/>, binding the values row by row,
    /// column by column, to <c>@p0</c>, <c>@p1</c>, ….
    /// </summary>
    public static string Insert(string? schema, string table, IReadOnlyList<string> columns, int rowCount)
    {
        var sql = new StringBuilder($"INSERT INTO {Table(schema, table)} ({ColumnList(columns)}) VALUES ");
        for (var row = 0; row < rowCount; row++)
        {
            if (row > 0)
            {
                sql.Append(", ");
            }

            sql.Append('(');
            for (var column = 0; column < columns.Count; column++)
            {
                if (column > 0)
                {
                    sql.Append(", ");
                }

                sql.Append(ParameterName(row * columns.Count + column));
            }

            sql.Append(')');
        }

        return sql.ToString();
    }

    /// <summary>
    /// <see cref="Insert"/>, where a row whose key already exists updates every non-key column instead. <c>VALUES(col)</c>
    /// is kept over the row alias of MySQL 8.0.19+ because MariaDB and MySQL 5.7 lack the alias (8.0.20+ only warns that it
    /// is deprecated). On a table whose columns are all key columns there is nothing to update, so the key is assigned to
    /// itself: an existing row stays as it is, while any other error still fails the statement (<c>INSERT IGNORE</c> would
    /// turn those into warnings).
    /// </summary>
    public static string Upsert(string? schema, string table, IReadOnlyList<string> columns, IReadOnlyList<string> keyColumns, int rowCount)
    {
        var nonKeyColumns = columns
            .Where(x => !keyColumns.Contains(x, StringComparer.Ordinal))
            .ToList();
        var assignments = nonKeyColumns.Count == 0
            ? $"{Quote(keyColumns[0])} = {Quote(keyColumns[0])}"
            : string.Join(", ", nonKeyColumns.Select(x => $"{Quote(x)} = VALUES({Quote(x)})"));

        return $"{Insert(schema, table, columns, rowCount)} ON DUPLICATE KEY UPDATE {assignments}";
    }

    /// <summary>
    /// A row's estimated parameter payload: a string counts its UTF-8 length, a byte array its length, NULL nothing and any
    /// other value <see cref="FixedValueSize"/> bytes.
    /// </summary>
    public static long EstimatedSize(object? value)
    {
        return value switch
        {
            null or DBNull => 0,
            string text => Encoding.UTF8.GetByteCount(text),
            byte[] bytes => bytes.Length,
            _ => FixedValueSize
        };
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
