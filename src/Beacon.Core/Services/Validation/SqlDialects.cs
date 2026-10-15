using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Single source of truth for mapping a Beacon database-engine name onto a SqlParserCS dialect.
/// Every SQL-parsing validator in Core (read-only validator, schema validator, row-limit rewriter)
/// resolves through here so a newly supported engine cannot silently fall back to
/// <see cref="GenericDialect"/> in one validator but not another.
/// </summary>
internal static class SqlDialects
{
    private static readonly HashSet<string> TSqlEngines = new(StringComparer.OrdinalIgnoreCase)
    {
        "mssql",
        "sqlserver",
        "microsoftsqlserver",
        "azuresynapse"
    };

    public static Dialect Resolve(string? dialect)
    {
        return (dialect ?? "").ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => new PostgreSqlDialect(),
            "sqlserver" or "mssql" or "microsoftsqlserver" or "azuresynapse" => new MsSqlDialect(),
            "mysql" or "mariadb" => new MySqlDialect(),
            "sqlite" => new SQLiteDialect(),
            "bigquery" => new BigQueryDialect(),
            "snowflake" => new SnowflakeDialect(),
            "databricks" => new DatabricksDialect(),
            "duckdb" => new DuckDbDialect(),
            _ => new GenericDialect()
        };
    }

    /// <summary>
    /// Engines whose lexer nests block comments the way SqlParserCS does (PostgreSQL, T-SQL). Every other engine —
    /// SQLite and MySQL among them — ends a block comment at its first <c>*/</c>.
    /// </summary>
    public static bool NestsBlockComments(string? dialect)
    {
        return dialect != null
            && (TSqlEngines.Contains(dialect)
                || dialect.Equals("postgresql", StringComparison.OrdinalIgnoreCase)
                || dialect.Equals("postgres", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// True when <paramref name="text"/> holds a marker that starts a line comment on the engine: <c>--</c> everywhere,
    /// <c>#</c> on MySQL and BigQuery, <c>//</c> on Snowflake. Quote-unaware, so a marker inside a literal counts too.
    /// </summary>
    public static bool HasLineCommentMarker(string text, string? dialect)
    {
        if (text.Contains("--"))
        {
            return true;
        }

        var parserDialect = Resolve(dialect);
        if (parserDialect is MySqlDialect or BigQueryDialect && text.Contains('#'))
        {
            return true;
        }

        return parserDialect is SnowflakeDialect && text.Contains("//");
    }

    /// <summary>
    /// T-SQL engines have no LIMIT keyword — they take the TOP / OFFSET-FETCH row-limit path.
    /// </summary>
    public static bool IsTSql(string? dialect)
    {
        return dialect != null && TSqlEngines.Contains(dialect);
    }
}
