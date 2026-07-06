using Microsoft.Extensions.Logging;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// AST-based read-only enforcement. At the MCP / semantic-search call sites the regex
/// <c>QueryGuardrailService</c> runs first as a backstop (§1.5), but at the query-builder and
/// SQL-connector call sites this validator is the SOLE read-only gate — so it fails CLOSED:
/// multi-statement SQL, any non-SELECT/EXPLAIN statement, data-modifying CTEs, SELECT ... INTO
/// (including inside UNION arms), and SQL the parser cannot handle are all rejected.
/// </summary>
public sealed class SqlReadOnlyAstValidator(ILogger<SqlReadOnlyAstValidator> logger)
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
            // Parse failure → REJECT (fail closed). This validator is the sole read-only gate at
            // the query-builder and connector call sites, so unparseable SQL is never allowed
            // through. Log the identifier/reason only — never the raw query text (§1.11).
            logger.LogWarning("AST read-only validation could not parse SQL ({Dialect}): {Message}", dialect, ex.Message);
            return $"Could not verify this SQL is read-only (parse error: {ex.Message}). Submit a valid SELECT query.";
        }

        if (statements.Count > 1)
        {
            return "Multiple SQL statements are not allowed. Submit a single SELECT query.";
        }

        foreach (var statement in statements)
        {
            var error = RejectNonReadOnlyStatement(statement);
            if (error != null)
            {
                return error;
            }
        }

        return null;
    }

    private static string? RejectNonReadOnlyStatement(Statement statement)
    {
        // EXPLAIN wraps an inner statement. On PostgreSQL/MySQL/Databricks, `EXPLAIN ANALYZE <DML>`
        // actually EXECUTES the wrapped write, so we must inspect the inner statement and apply the
        // SAME read-only check recursively — never accept EXPLAIN unconditionally.
        if (statement is Statement.Explain explain)
        {
            return RejectNonReadOnlyStatement(explain.Statement);
        }

        // ExplainTable (EXPLAIN <table> / DESCRIBE <table>) carries only a table name, not a wrapped
        // statement — it is inherently read-only metadata inspection, so allow it.
        if (statement is Statement.ExplainTable)
        {
            return null;
        }

        if (statement is not Statement.Select select)
        {
            // Surface the EXPLAIN-of-non-SELECT case with a targeted message; other statements fall
            // through to the generic "only SELECT" rejection.
            return $"Only SELECT queries are permitted. Found: {statement.GetType().Name}.";
        }

        return RejectNonReadOnlyQuery(select.Query);
    }

    private static string? RejectNonReadOnlyQuery(Query query)
    {
        // A WITH clause can smuggle a data-modifying CTE (WITH x AS (INSERT ... RETURNING ...))
        // that parses as an outer Select — walk every CTE body and require it too be read-only.
        if (query.With != null)
        {
            foreach (var cte in query.With.CteTables)
            {
                var cteError = RejectNonReadOnlyQuery(cte.Query);
                if (cteError != null)
                {
                    return cteError;
                }
            }
        }

        return RejectNonReadOnlyBody(query.Body);
    }

    private static string? RejectNonReadOnlyBody(SetExpression body)
    {
        // Data-modifying CTE bodies surface as SetExpression.Insert. (DELETE/UPDATE CTEs fail to
        // parse across dialects and are caught by the fail-closed parse path.)
        if (body is SetExpression.Insert)
        {
            return "Data-modifying CTEs (WITH ... AS (INSERT/UPDATE/DELETE ...)) are not allowed. Submit a read-only SELECT.";
        }

        // SELECT ... INTO <table> parses as a Select but materializes a new table in both
        // PostgreSQL and SQL Server — reject it (§1.5 read-only).
        if (body is SetExpression.SelectExpression { Select.Into: not null })
        {
            return "SELECT ... INTO is not allowed because it creates a table. Submit a read-only SELECT.";
        }

        // Recurse into UNION / set-operation arms so a data-mutation or SELECT ... INTO hidden in
        // one arm is still caught.
        if (body is SetExpression.SetOperation setOperation)
        {
            var leftError = RejectNonReadOnlyBody(setOperation.Left);
            if (leftError != null)
            {
                return leftError;
            }

            return RejectNonReadOnlyBody(setOperation.Right);
        }

        // A parenthesized subquery arm carries its own Query (with its own WITH clause).
        if (body is SetExpression.QueryExpression queryExpression)
        {
            return RejectNonReadOnlyQuery(queryExpression.Query);
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
            "sqlite" => new SQLiteDialect(),
            "bigquery" => new BigQueryDialect(),
            "snowflake" => new SnowflakeDialect(),
            "databricks" => new DatabricksDialect(),
            "duckdb" => new DuckDbDialect(),
            _ => new GenericDialect()
        };
    }
}
