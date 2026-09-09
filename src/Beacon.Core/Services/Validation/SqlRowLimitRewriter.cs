using System.Text.RegularExpressions;
using SqlParser;
using SqlParser.Ast;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Outcome of a row-limit rewrite attempt.
/// </summary>
public enum SqlRowLimitOutcome
{
    /// <summary>A row-limit clause was added to the outermost query.</summary>
    Applied,

    /// <summary>The outermost query already bounds its result (LIMIT / FETCH / TOP).</summary>
    AlreadyLimited,

    /// <summary>The statement cannot carry a row limit (blank SQL, non-query statement, maxRows &lt;= 0).</summary>
    NotApplicable,

    /// <summary>The SQL did not parse, so the legacy regex heuristic decided the placement.</summary>
    TextualFallback
}

/// <summary>Result of <see cref="SqlRowLimitRewriter.Apply"/>.</summary>
/// <param name="FallbackReason">Parser exception type name when <see cref="Outcome"/> is <see cref="SqlRowLimitOutcome.TextualFallback"/> — never the SQL text.</param>
public sealed record SqlRowLimitResult(string Sql, SqlRowLimitOutcome Outcome, string? FallbackReason = null);

/// <summary>
/// Caps the OUTERMOST result set of a query at <c>maxRows</c>. The decision (is it already bounded,
/// where does the clause belong) is made on the parsed AST, but the clause is applied to the original
/// SQL text — the returned SQL is the input bytes plus one inserted or appended clause, never a
/// round-trip through <c>ToSql()</c>. This keeps a caller's formatting, comments and dialect-specific
/// syntax intact while removing the regex false positives (a LIMIT inside a string literal or a
/// subquery, a TOP in an inner SELECT) that the purely textual heuristic could not distinguish.
/// </summary>
public static class SqlRowLimitRewriter
{
    // Matches the leading SELECT (with optional DISTINCT) of the trimmed SQL, skipping leading
    // whitespace, `--` line comments and `/* */` block comments so TOP lands on the outermost SELECT.
    private static readonly Regex LeadingSelectPattern = new(
        @"\A(?:\s|--[^\n]*(?:\n|$)|/\*.*?\*/)*SELECT(?:\s+DISTINCT)?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    // Legacy textual heuristics, retained only for the parse-failure fallback path.
    private static readonly Regex SelectKeywordPattern = new(
        @"\bSELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TextualLimitPattern = new(
        @"\bLIMIT\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TextualTopPattern = new(
        @"\bTOP\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TextualOrderByPattern = new(
        @"\bORDER\s+BY\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TextualSelectLeadingPattern = new(
        @"^\s*SELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <param name="sql">The query to bound.</param>
    /// <param name="maxRows">Row cap; zero or negative leaves the SQL untouched.</param>
    /// <param name="dialect">Database engine name (PostgreSQL, MSSQL, AzureSynapse, MySQL, …).</param>
    public static SqlRowLimitResult Apply(string sql, int maxRows, string? dialect)
    {
        if (maxRows <= 0 || string.IsNullOrWhiteSpace(sql))
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.NotApplicable);
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, SqlDialects.Resolve(dialect));
        }
        catch (Exception ex)
        {
            // Unparseable SQL still has to be bounded — the read-only gate decides whether it may run
            // at all, so this path must not silently return an uncapped query. The exception type (not the
            // message, which quotes SQL tokens) travels back so the gate can log the fallback (§1.11).
            return new SqlRowLimitResult(ApplyTextual(sql, maxRows, dialect), SqlRowLimitOutcome.TextualFallback, ex.GetType().Name);
        }

        if (statements.Count != 1)
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.NotApplicable);
        }

        var query = ResolveQuery(statements[0]);
        if (query == null)
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.NotApplicable);
        }

        // Only the OUTERMOST query counts: a LIMIT or TOP in a subquery bounds an intermediate result,
        // not what the caller receives.
        if (query.Limit != null || query.Fetch != null)
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.AlreadyLimited);
        }

        if (query.Body is SetExpression.SelectExpression { Select.Top: not null })
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.AlreadyLimited);
        }

        var trimmed = sql.TrimEnd().TrimEnd(';');

        if (SqlDialects.IsTSql(dialect))
        {
            return new SqlRowLimitResult(ApplyTSql(trimmed, query, maxRows), SqlRowLimitOutcome.Applied);
        }

        return new SqlRowLimitResult(AppendClause(trimmed, $"LIMIT {maxRows}"), SqlRowLimitOutcome.Applied);
    }

    private static Query? ResolveQuery(Statement statement)
    {
        // EXPLAIN wraps an inner statement; bounding the wrapped SELECT is what the caller asked for.
        if (statement is Statement.Explain explain)
        {
            return ResolveQuery(explain.Statement);
        }

        if (statement is Statement.Select select)
        {
            return select.Query;
        }

        // ExplainTable (DESCRIBE t) and every non-query statement carry no result set to cap.
        return null;
    }

    private static string ApplyTSql(string trimmed, Query query, int maxRows)
    {
        // `ORDER BY … OFFSET n ROWS` with no FETCH is legal T-SQL and already carries the OFFSET clause —
        // only the FETCH half is missing.
        if (query.Offset != null)
        {
            return AppendClause(trimmed, $"FETCH NEXT {maxRows} ROWS ONLY");
        }

        // Already ordered → OFFSET/FETCH bounds the outermost result without disturbing the ORDER BY.
        if (query.OrderBy != null)
        {
            return AppendClause(trimmed, $"OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY");
        }

        // A plain SELECT-leading query: cap the outermost SELECT with TOP. T-SQL requires
        // `SELECT DISTINCT TOP n`, never `TOP n DISTINCT`, so the match consumes DISTINCT too.
        if (query.With == null && query.Body is SetExpression.SelectExpression)
        {
            var match = LeadingSelectPattern.Match(trimmed);
            if (match.Success)
            {
                var insertAt = match.Index + match.Length;

                return $"{trimmed[..insertAt]} TOP {maxRows}{trimmed[insertAt..]}";
            }
        }

        // A CTE-leading query or a set operation: a leading TOP would land on the CTE body or the first
        // UNION arm and leave the OUTER result uncapped, and neither can be wrapped in a derived table.
        // Bound the outer result with a dummy-ordered OFFSET/FETCH (valid T-SQL) instead.
        return AppendClause(trimmed, $"ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY");
    }

    private static string AppendClause(string trimmed, string clause)
    {
        // A trailing `--` line comment would swallow an appended clause on the same line.
        var lastLine = trimmed[(trimmed.LastIndexOf('\n') + 1)..];
        var separator = lastLine.Contains("--") ? "\n" : " ";

        return $"{trimmed}{separator}{clause}";
    }

    private static string ApplyTextual(string sql, int maxRows, string? dialect)
    {
        var trimmed = sql.TrimEnd().TrimEnd(';');

        if (TextualLimitPattern.IsMatch(trimmed))
        {
            return sql;
        }

        if (TextualTopPattern.IsMatch(trimmed))
        {
            return sql;
        }

        if (SqlDialects.IsTSql(dialect))
        {
            if (TextualOrderByPattern.IsMatch(trimmed))
            {
                return $"{trimmed} OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY";
            }

            if (TextualSelectLeadingPattern.IsMatch(trimmed))
            {
                return SelectKeywordPattern.Replace(trimmed, $"SELECT TOP {maxRows}", 1);
            }

            return $"{trimmed} ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY";
        }

        return $"{trimmed} LIMIT {maxRows}";
    }
}
