using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// AST-based read-only enforcement. At the MCP / semantic-search call sites the regex
/// <c>QueryGuardrailService</c> runs first as a backstop (§1.5), but at the query-builder and
/// SQL-connector call sites this validator is the SOLE read-only gate — so it fails CLOSED:
/// multi-statement SQL, any non-SELECT/EXPLAIN statement, data-modifying CTEs, SELECT ... INTO,
/// row-locking clauses, side-effecting or cross-connection functions, names that reach a linked
/// server or storage, SQL the engine would lex differently from the parser, SQL in an unknown
/// dialect, and SQL the parser cannot handle are all rejected.
/// </summary>
public sealed class SqlReadOnlyAstValidator(
    ILogger<SqlReadOnlyAstValidator> logger,
    IOptions<McpDeploymentOptions>? deploymentOptions = null)
{
    // T-SQL table hints that take update or exclusive locks — the T-SQL counterpart of FOR UPDATE.
    private static readonly HashSet<string> TSqlLockingHints = new(StringComparer.OrdinalIgnoreCase)
    {
        "UPDLOCK", "XLOCK", "TABLOCKX"
    };

    private static readonly HashSet<string> FileFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "json", "parquet", "csv", "delta", "text", "orc", "avro", "binaryfile", "xml"
    };

    /// <summary>Longest SQL accepted: <c>Beacon:Mcp:Ceilings:MaxSqlChars</c>, the cap the MCP gate applies too.</summary>
    public int MaxSqlChars { get; } = (deploymentOptions?.Value.Ceilings ?? new McpCeilingOptions()).EffectiveMaxSqlChars;

    /// <param name="dialect">The data source's dialect name, see <see cref="DataSourceSqlDialect"/>. Null or unknown is rejected.</param>
    /// <returns>Null when the SQL passes; a rejection reason otherwise.</returns>
    public string? Validate(string sql, string? dialect)
    {
        if (string.IsNullOrWhiteSpace(sql))
        {
            return "Empty SQL is not a valid read-only statement. Submit a SELECT query.";
        }

        if (sql.Length > MaxSqlChars)
        {
            return $"SQL is {sql.Length} characters long; the limit is {MaxSqlChars}.";
        }

        // A generic grammar lexes strings and comments like no real engine, so its verdict would not describe what runs.
        var parserDialect = SqlDialects.Resolve(dialect);
        if (parserDialect is GenericDialect)
        {
            return "The SQL dialect of this data source is not known, so the SQL cannot be verified as read-only.";
        }

        var lexicalError = RejectLexicalDifferences(sql, dialect, parserDialect);
        if (lexicalError != null)
        {
            return lexicalError;
        }

        Sequence<Statement> statements;
        try
        {
            statements = SqlAst.Parse(sql, parserDialect);
        }
        catch (Exception ex)
        {
            // Parse failure → REJECT (fail closed). This validator is the sole read-only gate at
            // the query-builder and connector call sites, so unparseable SQL is never allowed
            // through. The parser's message quotes the SQL, so the log carries the exception type and
            // position only (§1.11); the SQL's author still gets the full message.
            var (line, column) = PositionOf(ex);
            logger.LogWarning(
                "AST read-only validation could not parse SQL ({Dialect}): {ExceptionType} at line {Line}, column {Column}",
                dialect,
                ex.GetType().Name,
                line,
                column);
            return $"Could not verify this SQL is read-only (parse error: {ex.Message}). Submit a valid SELECT query.";
        }

        if (statements.Count > 1)
        {
            return "Multiple SQL statements are not allowed. Submit a single SELECT query.";
        }

        if (statements.Count == 0)
        {
            return "The SQL holds no statement. Submit a single SELECT query.";
        }

        return RejectNonReadOnlyStatement(statements[0]) ?? RejectNonReadOnlyNodes(statements[0], parserDialect);
    }

    // The parser nests block comments, skips MySQL executable comments, always reads `--` as a comment, ends a line
    // comment only at LF, treats some Unicode whitespace as a separator and applies its own string-escape rules. An
    // engine that differs on any of these runs a different statement than the one validated here, so SQL relying on
    // such a difference is refused. Every scan is linear and ignores quoting on purpose: a quote-aware scan could be
    // steered by the very escapes it judges.
    private static string? RejectLexicalDifferences(string sql, string? dialect, Dialect parserDialect)
    {
        if (HasUnusualWhitespace(sql))
        {
            return "Only spaces, tabs and LF or CRLF line breaks may separate SQL: an engine may read other whitespace differently from the parser.";
        }

        if (!SqlDialects.NestsBlockComments(dialect) && OpensCommentInsideComment(sql))
        {
            return "Nested block comments are not allowed. Remove the inner /* ... */ comment.";
        }

        // The parser applies backslash escapes in some literals (N'...', X'...', MySQL and BigQuery strings) where an
        // engine may not — T-SQL, SQLite, MySQL in NO_BACKSLASH_ESCAPES mode, BigQuery raw strings — and skips them
        // where Databricks applies them, so a string can end at a different quote.
        if (HasBackslashBeforeQuote(sql))
        {
            return "A backslash before a quote is not allowed. Write a quote inside a string by doubling it ('').";
        }

        if (parserDialect is PostgreSqlDialect)
        {
            return RejectPostgreSqlLexicalDifferences(sql);
        }

        if (parserDialect is not MySqlDialect)
        {
            return null;
        }

        if (HasMySqlExecutableComment(sql))
        {
            return "MySQL executable comments (/*! ... */) and optimizer hints (/*+ ... */) are not allowed.";
        }

        if (HasMySqlNonCommentDoubleDash(sql))
        {
            return "On MySQL, -- starts a comment only when a space follows it. Add the space, or separate the minus signs.";
        }

        return null;
    }

    // U&'...' / U&"..." take a UESCAPE clause and escape rules of their own; a dollar-quote tag's characters are
    // matched byte-wise by PostgreSQL. Neither is worth the risk of reading a literal's end differently.
    private static string? RejectPostgreSqlLexicalDifferences(string sql)
    {
        if (HasUnicodeEscapePrefix(sql))
        {
            return "Unicode-escape strings and identifiers (U&'...', U&\"...\") are not allowed.";
        }

        return HasNonAsciiDollarTag(sql)
            ? "Dollar-quote tags may only use ASCII letters, digits and underscores."
            : null;
    }

    private static string? RejectNonReadOnlyStatement(Statement statement)
    {
        // EXPLAIN wraps an inner statement. On PostgreSQL/MySQL/Databricks, `EXPLAIN ANALYZE <DML>`
        // actually EXECUTES the wrapped write, so the inner statement gets the SAME read-only check —
        // never accept EXPLAIN unconditionally.
        while (statement is Statement.Explain explain)
        {
            statement = explain.Statement;
        }

        // ExplainTable (EXPLAIN <table> / DESCRIBE <table>) carries only a table name, not a wrapped
        // statement — it is inherently read-only metadata inspection, so allow it.
        if (statement is Statement.ExplainTable or Statement.Select)
        {
            return null;
        }

        return $"Only SELECT queries are permitted. Found: {statement.GetType().Name}.";
    }

    // Every node of the statement — CTE bodies, UNION arms, derived tables, subqueries, function arguments — not just its
    // top-level shape. The walk is iterative, and the parse has already refused trees deeper than SqlAst.MaxDepth.
    private static string? RejectNonReadOnlyNodes(Statement statement, Dialect dialect)
    {
        foreach (var node in SqlAst.Nodes(statement))
        {
            var error = node switch
            {
                // Data-modifying CTE bodies surface as SetExpression.Insert. (DELETE/UPDATE CTEs fail to
                // parse across dialects and are caught by the fail-closed parse path.)
                SetExpression.Insert => "Data-modifying CTEs (WITH ... AS (INSERT/UPDATE/DELETE ...)) are not allowed. Submit a read-only SELECT.",

                // SELECT ... INTO <table> parses as a Select but materializes a new table in both
                // PostgreSQL and SQL Server — reject it (§1.5 read-only).
                Select { Into: not null } => "SELECT ... INTO is not allowed because it creates a table. Submit a read-only SELECT.",
                Query { Locks.Count: > 0 } => "Row-locking clauses (FOR UPDATE / FOR SHARE) are not allowed in read-only SQL.",
                Expression.Function function => RejectFunction(function.Name, dialect),
                TableFactor.Function function => RejectFunction(function.Name, dialect),
                TableFactor.Table table => RejectTable(table, dialect),
                Value.DollarQuotedString dollarQuoted => RejectDollarQuoted(dollarQuoted.Value, dialect),
                Value.EscapedStringLiteral or Value.UnicodeStringLiteral when dialect is not PostgreSqlDialect =>
                    "E'...' and U&'...' string literals are only allowed on PostgreSQL.",
                _ => null
            };

            if (error != null)
            {
                return error;
            }
        }

        return null;
    }

    // The tokenizer reads $$...$$ in every dialect, but only PostgreSQL (tagged or not) and Snowflake (untagged) have it.
    private static string? RejectDollarQuoted(DollarQuotedStringValue value, Dialect dialect)
    {
        var allowed = dialect is PostgreSqlDialect || (dialect is SnowflakeDialect && string.IsNullOrEmpty(value.Tag));

        return allowed
            ? null
            : "Dollar-quoted strings are only allowed on PostgreSQL, and on Snowflake without a tag.";
    }

    private static string? RejectFunction(ObjectName name, Dialect dialect)
    {
        return RejectDeniedFunction(name, dialect) ?? RejectLinkedServerName(name, dialect);
    }

    private static string? RejectTable(TableFactor.Table table, Dialect dialect)
    {
        // A table source with arguments is a table-valued function: OPENQUERY(...), read_files(...), EXTERNAL_QUERY(...).
        if (table.Args != null)
        {
            var functionError = RejectDeniedFunction(table.Name, dialect);
            if (functionError != null)
            {
                return functionError;
            }
        }

        if (dialect is MsSqlDialect)
        {
            return RejectLinkedServerName(table.Name, dialect) ?? RejectTSqlLockingHints(table);
        }

        if (dialect is DatabricksDialect && IsPathReference(table.Name))
        {
            return "File and storage path references (format.`path`) are not allowed. Query a catalog table instead.";
        }

        return null;
    }

    // T-SQL's server.database.schema.object reaches a linked server. database.schema.object stays allowed: it reads
    // another database on the same server, bounded by the data source login's grants.
    private static string? RejectLinkedServerName(ObjectName name, Dialect dialect)
    {
        return dialect is MsSqlDialect && name.Values.Count >= 4
            ? "Names with four or more parts are not allowed: they reach a linked server. Use schema.table or database.schema.table."
            : null;
    }

    private static string? RejectTSqlLockingHints(TableFactor.Table table)
    {
        // WITH (UPDLOCK) and the older bare form t (UPDLOCK), which parses as table arguments.
        var hints = new object?[] { table.WithHints, table.Args }
            .Where(x => x != null)
            .SelectMany(x => SqlAst.Nodes(x!))
            .OfType<Expression.Identifier>();

        return hints.Any(x => TSqlLockingHints.Contains(x.Ident.Value))
            ? "Locking table hints (UPDLOCK, XLOCK, TABLOCKX) are not allowed in read-only SQL."
            : null;
    }

    // Databricks reads files directly through format.`path` or a backtick-quoted URI. A quoted schema that is merely
    // named like a format (`text`.`orders`) is an ordinary table.
    private static bool IsPathReference(ObjectName name)
    {
        var parts = name.Values;
        if (parts.Any(x => x.QuoteStyle == '`' && (x.Value.Contains('/') || x.Value.Contains(':'))))
        {
            return true;
        }

        return parts.Count == 2
            && parts[0].QuoteStyle == null
            && parts[1].QuoteStyle == '`'
            && FileFormats.Contains(parts[0].Value);
    }

    private static string? RejectDeniedFunction(ObjectName name, Dialect dialect)
    {
        var lastPart = name.Values.Count > 0 ? name.Values[^1].Value : "";

        return SqlDeniedFunctions.IsDenied(lastPart, dialect)
            ? $"The function {lastPart} is not allowed in read-only SQL: it reaches files, other servers or settings, or holds resources."
            : null;
    }

    // Any `/*` before the `*/` closing an earlier `/*` counts, which also rejects the rare literal holding two `/*`.
    // Linear: each step moves past the previous opener, and a `*/` search that runs past the next opener ends the scan.
    private static bool OpensCommentInsideComment(string sql)
    {
        var open = sql.IndexOf("/*", StringComparison.Ordinal);
        while (open >= 0)
        {
            var next = sql.IndexOf("/*", open + 2, StringComparison.Ordinal);
            if (next < 0)
            {
                return false;
            }

            var close = sql.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0 || next < close)
            {
                return true;
            }

            open = next;
        }

        return false;
    }

    // Space, tab, LF and CRLF only. A lone CR ends a -- comment on some engines but not for the parser; other Unicode
    // whitespace may separate tokens for the parser where an engine reads it as part of a name or a byte sequence.
    private static bool HasUnusualWhitespace(string sql)
    {
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            var loneCarriageReturn = c == '\r' && (i + 1 == sql.Length || sql[i + 1] != '\n');
            if (loneCarriageReturn || (char.IsWhiteSpace(c) && c is not (' ' or '\t' or '\n' or '\r')))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnicodeEscapePrefix(string sql)
    {
        for (var i = 0; i + 2 < sql.Length; i++)
        {
            if (sql[i] is 'u' or 'U' && sql[i + 1] == '&' && sql[i + 2] is '\'' or '"')
            {
                return true;
            }
        }

        return false;
    }

    // A run of tag characters between two `$` holding anything outside ASCII. Each scan stops at the next `$` or at a
    // character that cannot be part of a tag, so the whole check is linear.
    private static bool HasNonAsciiDollarTag(string sql)
    {
        var dollar = sql.IndexOf('$');
        while (dollar >= 0)
        {
            var end = dollar + 1;
            var nonAscii = false;
            while (end < sql.Length && (char.IsAsciiLetterOrDigit(sql[end]) || sql[end] == '_' || sql[end] > 0x7F))
            {
                nonAscii |= sql[end] > 0x7F;
                end++;
            }

            if (nonAscii && end < sql.Length && sql[end] == '$')
            {
                return true;
            }

            dollar = sql.IndexOf('$', end);
        }

        return false;
    }

    private static bool HasBackslashBeforeQuote(string sql)
    {
        for (var i = 0; i + 1 < sql.Length; i++)
        {
            if (sql[i] == '\\' && sql[i + 1] is '\'' or '"' or '`')
            {
                return true;
            }
        }

        return false;
    }

    // `/*!` and MariaDB's `/*M!` hold code MySQL runs; `/*+` holds optimizer hints. The parser skips all three as comments.
    private static bool HasMySqlExecutableComment(string sql)
    {
        var open = sql.IndexOf("/*", StringComparison.Ordinal);
        while (open >= 0)
        {
            var rest = sql.AsSpan(open + 2);
            if (rest.StartsWith("!") || rest.StartsWith("+") || rest.StartsWith("M!", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            open = sql.IndexOf("/*", open + 2, StringComparison.Ordinal);
        }

        return false;
    }

    // MySQL reads `--` as a comment only when an ASCII space or control byte follows (`1--1` is 1 - -1; MySQL works on
    // bytes, so a Unicode space does not count); the parser always reads a comment, so the text after such a `--` would
    // run without having been validated.
    private static bool HasMySqlNonCommentDoubleDash(string sql)
    {
        var dash = sql.IndexOf("--", StringComparison.Ordinal);
        while (dash >= 0)
        {
            var after = dash + 2;
            if (after == sql.Length || !(sql[after] == ' ' || sql[after] < 0x20 || sql[after] == 0x7F))
            {
                return true;
            }

            dash = sql.IndexOf("--", dash + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static (long Line, long Column) PositionOf(Exception ex)
    {
        return ex switch
        {
            ParserException parser => (parser.Line, parser.Column),
            TokenizeException tokenizer => (tokenizer.Line, tokenizer.Column),
            _ => (0, 0)
        };
    }
}
