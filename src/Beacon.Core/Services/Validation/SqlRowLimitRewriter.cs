using System.Text;
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
    TextualFallback,

    /// <summary>The SQL parsed but is nested too deeply to place a cap safely; it must not run.</summary>
    Refused
}

/// <summary>Result of <see cref="SqlRowLimitRewriter.Apply"/>.</summary>
/// <param name="FallbackReason">Parser exception type name when <see cref="Outcome"/> is <see cref="SqlRowLimitOutcome.TextualFallback"/> or <see cref="SqlRowLimitOutcome.Refused"/> — never the SQL text.</param>
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
            statements = SqlAst.Parse(sql, dialect);
        }
        catch (SqlAst.TooDeepException ex)
        {
            // Not a parser gap: the textual heuristic cannot see where such a statement ends either, so no cap is placed
            // and the caller must not run it.
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.Refused, ex.GetType().Name);
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
            return new SqlRowLimitResult(ApplyTSql(trimmed, query, maxRows, dialect), SqlRowLimitOutcome.Applied);
        }

        return new SqlRowLimitResult(AppendClause(trimmed, $"LIMIT {maxRows}", dialect), SqlRowLimitOutcome.Applied);
    }

    /// <summary>
    /// The index just past the leading <c>SELECT</c> of <paramref name="sql"/> (and a <c>DISTINCT</c> right after
    /// it), or -1 when the SQL does not open with one. Skips whitespace, <c>--</c> line comments and <c>/* */</c>
    /// block comments, counting nesting depth the way T-SQL does — an inner <c>*/</c> closes only the innermost
    /// comment, so a SELECT written inside a nested comment is never taken for the real one. One forward pass,
    /// linear on any input. Internal for unit tests.
    /// </summary>
    internal static int FindLeadingSelectEnd(string sql)
    {
        var position = SkipLeadingTrivia(sql);
        if (position < 0 || !IsKeywordAt(sql, position, "SELECT"))
        {
            return -1;
        }

        var selectEnd = position + "SELECT".Length;
        var next = selectEnd;
        while (next < sql.Length && char.IsWhiteSpace(sql[next]))
        {
            next++;
        }

        return next > selectEnd && IsKeywordAt(sql, next, "DISTINCT")
            ? next + "DISTINCT".Length
            : selectEnd;
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

    private static string ApplyTSql(string trimmed, Query query, int maxRows, string? dialect)
    {
        // `ORDER BY … OFFSET n ROWS` with no FETCH is legal T-SQL and already carries the OFFSET clause —
        // only the FETCH half is missing.
        if (query.Offset != null)
        {
            return AppendClause(trimmed, $"FETCH NEXT {maxRows} ROWS ONLY", dialect);
        }

        // Already ordered → OFFSET/FETCH bounds the outermost result without disturbing the ORDER BY.
        if (query.OrderBy != null)
        {
            return AppendClause(trimmed, $"OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY", dialect);
        }

        // A plain SELECT-leading query: cap the outermost SELECT with TOP. T-SQL requires
        // `SELECT DISTINCT TOP n`, never `TOP n DISTINCT`, so the match consumes DISTINCT too.
        if (query.With == null && query.Body is SetExpression.SelectExpression)
        {
            var insertAt = FindLeadingSelectEnd(trimmed);
            if (insertAt >= 0)
            {
                return $"{trimmed[..insertAt]} TOP {maxRows}{trimmed[insertAt..]}";
            }
        }

        // A CTE-leading query or a set operation: a leading TOP would land on the CTE body or the first
        // UNION arm and leave the OUTER result uncapped, and neither can be wrapped in a derived table.
        // Bound the outer result with a dummy-ordered OFFSET/FETCH (valid T-SQL) instead.
        return AppendClause(trimmed, $"ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY", dialect);
    }

    // The index of the first character after leading whitespace and comments; -1 when a block comment never closes.
    private static int SkipLeadingTrivia(string sql)
    {
        var position = 0;
        while (position < sql.Length)
        {
            if (char.IsWhiteSpace(sql[position]))
            {
                position++;
                continue;
            }

            var rest = sql.AsSpan(position);
            if (rest.StartsWith("--"))
            {
                var lineEnd = sql.IndexOf('\n', position);
                position = lineEnd < 0 ? sql.Length : lineEnd + 1;
                continue;
            }

            if (!rest.StartsWith("/*"))
            {
                return position;
            }

            position = SkipBlockComment(sql, position);
            if (position < 0)
            {
                return -1;
            }
        }

        return position;
    }

    // T-SQL nests block comments: every `/*` needs its own `*/`. Returns the index after the outer `*/`, or -1.
    private static int SkipBlockComment(string sql, int start)
    {
        var depth = 0;
        var position = start;
        while (position < sql.Length - 1)
        {
            var pair = sql.AsSpan(position, 2);
            if (pair is "/*")
            {
                depth++;
                position += 2;
            }
            else if (pair is "*/")
            {
                depth--;
                position += 2;
                if (depth == 0)
                {
                    return position;
                }
            }
            else
            {
                position++;
            }
        }

        return -1;
    }

    private static bool IsKeywordAt(string sql, int position, string keyword)
    {
        var end = position + keyword.Length;

        return sql.AsSpan(position).StartsWith(keyword, StringComparison.OrdinalIgnoreCase)
            && (end == sql.Length || !(char.IsLetterOrDigit(sql[end]) || sql[end] is '_' or '@' or '#' or '$'));
    }

    private static string AppendClause(string trimmed, string clause, string? dialect)
    {
        // A trailing line comment (`--`, or the engine's `#` / `//`) would swallow an appended clause on the same line.
        var lastLine = trimmed[(trimmed.LastIndexOf('\n') + 1)..];
        var separator = SqlDialects.HasLineCommentMarker(lastLine, dialect) ? "\n" : " ";

        return $"{trimmed}{separator}{clause}";
    }

    private static string ApplyTextual(string sql, int maxRows, string? dialect)
    {
        var trimmed = sql.TrimEnd().TrimEnd(';');

        // Only a LIMIT/TOP outside literals, quoted names and comments counts as an existing bound. When an engine could
        // end a literal somewhere else, no textual bound is trusted and the cap is added anyway: a second bound at worst
        // fails the statement, a missed one leaves it uncapped.
        var code = CodeOnly(trimmed);
        if (code != null && (TextualLimitPattern.IsMatch(code) || TextualTopPattern.IsMatch(code)))
        {
            return sql;
        }

        if (SqlDialects.IsTSql(dialect))
        {
            if (code != null && TextualOrderByPattern.IsMatch(code))
            {
                return AppendClause(trimmed, $"OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY", dialect);
            }

            if (TextualSelectLeadingPattern.IsMatch(trimmed))
            {
                return SelectKeywordPattern.Replace(trimmed, $"SELECT TOP {maxRows}", 1);
            }

            return AppendClause(trimmed, $"ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY", dialect);
        }

        return AppendClause(trimmed, $"LIMIT {maxRows}", dialect);
    }

    // The text with every literal, quoted name and comment blanked to a space, or null when the quoting is ambiguous
    // across engines (a backslash before a quote, a dollar quote, a triple quote). Every comment marker any engine knows
    // counts, so the scan blanks more text rather than less. One forward pass.
    private static string? CodeOnly(string sql)
    {
        if (sql.Contains("'''") || sql.Contains("\"\"\"") || HasBackslashBeforeQuote(sql) || HasDollarQuote(sql))
        {
            return null;
        }

        var code = new StringBuilder(sql.Length);
        var position = 0;
        while (position < sql.Length)
        {
            var end = EndOfNonCode(sql, position);
            if (end == position)
            {
                code.Append(sql[position]);
                position++;
                continue;
            }

            code.Append(' ');
            position = end;
        }

        return code.ToString();
    }

    // The index just past the literal, quoted name or comment starting at position; position itself when none does.
    private static int EndOfNonCode(string sql, int position)
    {
        var rest = sql.AsSpan(position);
        if (rest.StartsWith("--") || rest.StartsWith("//") || rest[0] == '#')
        {
            var lineEnd = sql.IndexOfAny(['\r', '\n'], position);

            return lineEnd < 0 ? sql.Length : lineEnd;
        }

        if (rest.StartsWith("/*"))
        {
            var commentEnd = SkipBlockComment(sql, position);

            return commentEnd < 0 ? sql.Length : commentEnd;
        }

        return rest[0] switch
        {
            '\'' or '"' or '`' => EndOfQuoted(sql, position, rest[0]),
            '[' => EndOfQuoted(sql, position, ']'),
            _ => position
        };
    }

    // Quoted text ends at the first closing character that is not doubled; unclosed, it runs to the end.
    private static int EndOfQuoted(string sql, int open, char close)
    {
        var position = open + 1;
        while (position < sql.Length)
        {
            var next = sql.IndexOf(close, position);
            if (next < 0)
            {
                return sql.Length;
            }

            if (next + 1 < sql.Length && sql[next + 1] == close)
            {
                position = next + 2;
                continue;
            }

            return next + 1;
        }

        return sql.Length;
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

    // `$$` or `$tag$`: each scan stops at the next `$` or at a character that cannot be part of a tag.
    private static bool HasDollarQuote(string sql)
    {
        var dollar = sql.IndexOf('$');
        while (dollar >= 0)
        {
            var end = dollar + 1;
            while (end < sql.Length && (char.IsLetterOrDigit(sql[end]) || sql[end] == '_'))
            {
                end++;
            }

            if (end < sql.Length && sql[end] == '$')
            {
                return true;
            }

            dollar = sql.IndexOf('$', end);
        }

        return false;
    }
}
