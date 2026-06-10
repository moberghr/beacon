using Microsoft.Extensions.Logging;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.MCP.Services;

/// <summary>
/// AST-based read-only enforcement, layered as defense-in-depth AFTER the regex
/// <c>QueryGuardrailService</c> (§1.5 — never replaces it). Rejects multi-statement SQL and any
/// statement that is not SELECT/EXPLAIN. SQL the parser cannot handle is allowed through —
/// the regex guardrail has already run and stays authoritative.
/// </summary>
internal sealed class SqlReadOnlyAstValidator(ILogger<SqlReadOnlyAstValidator> logger)
{
    /// <returns>Null when the SQL passes; a rejection reason otherwise.</returns>
    public string? Validate(string sql, string? dialect = null)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return null;
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, ResolveDialect(dialect));
        }
        catch (Exception ex)
        {
            // Parse failure → allow; the regex guardrail remains authoritative
            logger.LogWarning("AST read-only validation could not parse SQL ({Dialect}): {Message}", dialect, ex.Message);
            return null;
        }

        if (statements.Count > 1)
        {
            return "Multiple SQL statements are not allowed. Submit a single SELECT query.";
        }

        foreach (var statement in statements)
        {
            if (statement is not (Statement.Select or Statement.Explain or Statement.ExplainTable))
            {
                return $"Only SELECT queries are permitted. Found: {statement.GetType().Name}.";
            }
        }

        return null;
    }

    private static Dialect ResolveDialect(string? dialect)
    {
        return (dialect ?? "").ToLowerInvariant() switch
        {
            "postgresql" or "postgres" => new PostgreSqlDialect(),
            "sqlserver" or "mssql" or "microsoftsqlserver" or "azuresynapse" => new MsSqlDialect(),
            "mysql" or "mariadb" => new MySqlDialect(),
            "bigquery" => new BigQueryDialect(),
            "snowflake" => new SnowflakeDialect(),
            "databricks" => new DatabricksDialect(),
            "duckdb" => new DuckDbDialect(),
            _ => new GenericDialect()
        };
    }
}
