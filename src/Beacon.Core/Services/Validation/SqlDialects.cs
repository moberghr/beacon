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
    /// T-SQL engines have no LIMIT keyword — they take the TOP / OFFSET-FETCH row-limit path.
    /// </summary>
    public static bool IsTSql(string? dialect)
    {
        return dialect != null && TSqlEngines.Contains(dialect);
    }
}
