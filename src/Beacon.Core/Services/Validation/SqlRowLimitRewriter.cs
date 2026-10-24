using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Tokens;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Outcome of a row-limit rewrite attempt.
/// </summary>
public enum SqlRowLimitOutcome
{
    /// <summary>A row-limit clause was added to the outermost query.</summary>
    Applied,

    /// <summary>The outermost query already bounds its result (LIMIT / FETCH / TOP) with a number no larger than the cap.</summary>
    AlreadyLimited,

    /// <summary>
    /// The outermost query bounds its result above the cap or in a way that does not keep it within the cap (LIMIT ALL /
    /// NULL, an expression, TOP … PERCENT, WITH TIES): its bound was lowered to the cap, or the statement was wrapped in a
    /// derived table capped by an outer bound.
    /// </summary>
    OwnBoundCapped,

    /// <summary>The statement cannot carry a row limit (blank SQL, non-query statement).</summary>
    NotApplicable,

    /// <summary>The SQL did not parse, so the legacy regex heuristic decided the placement.</summary>
    TextualFallback,

    /// <summary>
    /// The statement must not run: the SQL parsed but is nested too deeply to place a cap safely, its own bound cannot be
    /// capped, or the cap itself is zero or below.
    /// </summary>
    Refused
}

/// <summary>Result of <see cref="SqlRowLimitRewriter.Apply"/>.</summary>
/// <param name="FallbackReason">Parser exception type name when <see cref="Outcome"/> is <see cref="SqlRowLimitOutcome.TextualFallback"/> or <see cref="SqlRowLimitOutcome.Refused"/> — never the SQL text.</param>
public sealed record SqlRowLimitResult(string Sql, SqlRowLimitOutcome Outcome, string? FallbackReason = null)
{
    /// <summary>Why a <see cref="SqlRowLimitOutcome.Refused"/> statement must not run; null otherwise.</summary>
    public string? RefusalMessage { get; init; }

    /// <summary>The gate's verdict code for a <see cref="SqlRowLimitOutcome.Refused"/> statement; null for a statement too deep to cap.</summary>
    public string? RefusalCode { get; init; }
}

/// <summary>
/// Caps the OUTERMOST result set of a query at <c>maxRows</c>. The decision (is it already bounded,
/// where does the clause belong) is made on the parsed AST, but the clause is applied to the original
/// SQL text — the returned SQL is the input bytes plus one inserted or appended clause, the count of the
/// statement's own bound lowered in place, or the statement wrapped in a capped derived table; never a
/// round-trip through <c>ToSql()</c>. This keeps a caller's formatting, comments and dialect-specific
/// syntax intact while removing the regex false positives (a LIMIT inside a string literal or a
/// subquery, a TOP in an inner SELECT) that the purely textual heuristic could not distinguish. The
/// statement's own bound counts only when it is a whole number no larger than the cap.
/// </summary>
public static class SqlRowLimitRewriter
{
    public const string InvalidRowCapMessage = "The row cap must be at least 1 row.";

    // Legacy textual heuristics, retained only for the parse-failure fallback path.
    private static readonly Regex SelectKeywordPattern = new(
        @"\bSELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private static readonly Regex TextualLimitPattern = new(
        @"\bLIMIT\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private static readonly Regex TextualTopPattern = new(
        @"\bTOP\s+\d+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private static readonly Regex TextualOrderByPattern = new(
        @"\bORDER\s+BY\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    private static readonly Regex TextualSelectLeadingPattern = new(
        @"^\s*SELECT\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled, TimeSpan.FromMilliseconds(1000));

    /// <param name="sql">The query to bound.</param>
    /// <param name="maxRows">Row cap; zero or below is refused, never read as "no cap".</param>
    /// <param name="dialect">Database engine name (PostgreSQL, MSSQL, AzureSynapse, MySQL, …).</param>
    public static SqlRowLimitResult Apply(string sql, int maxRows, string? dialect)
    {
        if (maxRows <= 0)
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.Refused)
            {
                RefusalCode = SqlGateCodes.InvalidRowCap,
                RefusalMessage = InvalidRowCapMessage
            };
        }

        if (string.IsNullOrWhiteSpace(sql))
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.NotApplicable);
        }

        Sequence<Statement> statements;
        try
        {
            statements = SqlAst.Parse(sql, dialect);
        }
        catch (SqlAst.UnverifiableSqlException ex)
        {
            // Not a parser gap: the textual heuristic cannot see where such a statement ends either, so no cap is placed
            // and the caller must not run it.
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.Refused, ex.GetType().Name) { RefusalMessage = ex.Message };
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

        // Only the OUTERMOST query counts: a LIMIT or TOP in a subquery bounds an intermediate result, not what the caller
        // receives. Its own bound counts only when it is a whole number no larger than the cap.
        var bound = BoundOf(query, maxRows);
        if (bound == OuterBound.None && sql.Contains("LIMIT", StringComparison.OrdinalIgnoreCase) && HasOuterKeyword(sql, dialect, Keyword.LIMIT))
        {
            // LIMIT ALL leaves no bound in the tree.
            bound = OuterBound.Unbounded;
        }

        if (bound == OuterBound.WithinCap)
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.AlreadyLimited);
        }

        if (bound == OuterBound.Unbounded)
        {
            return CapOwnBound(sql, statements[0], query, maxRows, dialect);
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

    // A whole-number bound no larger than the cap bounds the result; any bound within the cap does, also next to another.
    // LIMIT NULL, a placeholder, an expression, a number larger than the cap, TOP … PERCENT and WITH TIES do not.
    private static OuterBound BoundOf(Query query, int maxRows)
    {
        var bounds = new List<bool>();
        if (query.Limit != null)
        {
            bounds.Add(IsCountWithin(query.Limit, maxRows));
        }

        if (query.Fetch is { } fetch)
        {
            bounds.Add(fetch is { Percent: false, WithTies: false } && (fetch.Quantity == null || IsCountWithin(fetch.Quantity, maxRows)));
        }

        if (query.Body is SetExpression.SelectExpression { Select.Top: { } top })
        {
            var quantityWithin = top.Quantity switch
            {
                TopQuantity.Constant constant => constant.Quantity >= 0 && constant.Quantity <= maxRows,
                TopQuantity.TopExpression expression => IsCountWithin(expression.Expression, maxRows),
                _ => false
            };
            bounds.Add(top is { Percent: false, WithTies: false } && quantityWithin);
        }

        if (bounds.Count == 0)
        {
            return OuterBound.None;
        }

        return bounds.Any(x => x) ? OuterBound.WithinCap : OuterBound.Unbounded;
    }

    private static bool IsCountWithin(Expression expression, int maxRows)
    {
        return expression is Expression.LiteralValue { Value: Value.Number number }
            && long.TryParse(number.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            && count <= maxRows;
    }

    /// <summary>
    /// The outermost query bounds its rows in a way that does not keep them within the cap. A bound whose count is a single
    /// token — a number, <c>ALL</c> or <c>NULL</c> after LIMIT, a number after FETCH FIRST/NEXT or TOP — is lowered to the
    /// cap in place, so its ORDER BY and everything else stay as written. Any other bound (an expression, a placeholder,
    /// TOP … PERCENT, WITH TIES) is wrapped: the statement becomes a derived table capped by an outer LIMIT, or TOP on
    /// T-SQL. T-SQL cannot wrap a statement with a WITH clause, so such a statement is refused. Either way the result is
    /// parsed again and must carry a bound within the cap.
    /// </summary>
    private static SqlRowLimitResult CapOwnBound(string sql, Statement statement, Query query, int maxRows, string? dialect)
    {
        var lowered = LowerOwnBound(sql, query, maxRows, dialect);
        if (lowered != null && IsBoundWithin(lowered, maxRows, dialect))
        {
            return new SqlRowLimitResult(lowered, SqlRowLimitOutcome.OwnBoundCapped);
        }

        // EXPLAIN returns the plan, not the rows, and cannot be wrapped.
        if (statement is Statement.Explain)
        {
            return new SqlRowLimitResult(sql, SqlRowLimitOutcome.AlreadyLimited);
        }

        var trimmed = sql.TrimEnd().TrimEnd(';');
        var tSql = SqlDialects.IsTSql(dialect);
        var wrapped = tSql
            ? query.With == null ? $"SELECT TOP {maxRows} * FROM (\n{trimmed}\n) AS beacon_rows" : null
            : $"SELECT * FROM (\n{trimmed}\n) AS beacon_rows LIMIT {maxRows}";
        if (wrapped != null && IsBoundWithin(wrapped, maxRows, dialect))
        {
            return new SqlRowLimitResult(wrapped, SqlRowLimitOutcome.OwnBoundCapped);
        }

        return new SqlRowLimitResult(sql, SqlRowLimitOutcome.Refused, nameof(CapOwnBound))
        {
            RefusalCode = SqlGateCodes.UncappableBound,
            RefusalMessage = $"The statement bounds its own rows in a way the row cap cannot check or place (an expression, PERCENT or WITH TIES). Bound it with a number of at most {maxRows} rows, or remove the bound."
        };
    }

    private static bool IsBoundWithin(string sql, int maxRows, string? dialect)
    {
        try
        {
            var statements = SqlAst.Parse(sql, dialect);
            var query = statements.Count == 1 ? ResolveQuery(statements[0]) : null;

            return query != null && BoundOf(query, maxRows) == OuterBound.WithinCap;
        }
        catch (Exception)
        {
            return false;
        }
    }

    // The bound's count token replaced by the cap, or null when the bound has no single-token count to replace.
    private static string? LowerOwnBound(string sql, Query query, int maxRows, string? dialect)
    {
        var limitCount = query.Limit is null or Expression.LiteralValue { Value: Value.Number or Value.Null };
        var fetchCount = query.Fetch is { Percent: false, WithTies: false, Quantity: Expression.LiteralValue { Value: Value.Number } };
        var topCount = query.Body is SetExpression.SelectExpression { Select.Top: { Percent: false, WithTies: false } top }
            && top.Quantity is TopQuantity.Constant or TopQuantity.TopExpression { Expression: Expression.LiteralValue { Value: Value.Number } };

        var tokens = OuterTokens(sql, dialect);
        if (tokens == null)
        {
            return null;
        }

        var target = query.Limit != null || (query.Fetch == null && !topCount)
            ? limitCount ? LimitCountToken(tokens) : null
            : query.Fetch != null
                ? fetchCount ? FetchCountToken(tokens) : null
                : TopCountToken(tokens);
        if (target is not { } span)
        {
            return null;
        }

        return string.Concat(sql.AsSpan(0, span.Offset), maxRows.ToString(CultureInfo.InvariantCulture), sql.AsSpan(span.Offset + span.Length));
    }

    // The token after the last LIMIT at depth 0 — after the comma in MySQL's LIMIT offset, count.
    private static (int Offset, int Length)? LimitCountToken(List<OuterToken> tokens)
    {
        var limit = tokens.FindLastIndex(x => x.Depth == 0 && x.Token is Word { QuoteStyle: null, Keyword: Keyword.LIMIT });
        if (limit < 0 || limit + 1 >= tokens.Count)
        {
            return null;
        }

        var count = limit + 1;
        if (count + 2 < tokens.Count && tokens[count + 1].Token is Comma)
        {
            count += 2;
        }

        return IsCountToken(tokens[count].Token) ? (tokens[count].Offset, tokens[count].Length) : null;
    }

    // The number after the last FETCH FIRST|NEXT at depth 0.
    private static (int Offset, int Length)? FetchCountToken(List<OuterToken> tokens)
    {
        var fetch = tokens.FindLastIndex(x => x.Depth == 0 && x.Token is Word { QuoteStyle: null, Keyword: Keyword.FETCH });
        if (fetch < 0 || fetch + 2 >= tokens.Count || tokens[fetch + 1].Token is not Word { Keyword: Keyword.FIRST or Keyword.NEXT })
        {
            return null;
        }

        return tokens[fetch + 2].Token is Number ? (tokens[fetch + 2].Offset, tokens[fetch + 2].Length) : null;
    }

    // The number after TOP (or inside TOP ( … )) of the first SELECT at depth 0 — the outermost query's own SELECT, since
    // CTE bodies sit inside parentheses.
    private static (int Offset, int Length)? TopCountToken(List<OuterToken> tokens)
    {
        var select = tokens.FindIndex(x => x.Depth == 0 && x.Token is Word { QuoteStyle: null, Keyword: Keyword.SELECT });
        var position = select + 1;
        while (position < tokens.Count && tokens[position].Token is Word { QuoteStyle: null, Keyword: Keyword.DISTINCT or Keyword.ALL })
        {
            position++;
        }

        if (select < 0 || position + 1 >= tokens.Count || tokens[position].Token is not Word { QuoteStyle: null, Keyword: Keyword.TOP })
        {
            return null;
        }

        var count = tokens[position + 1].Token is LeftParen ? position + 2 : position + 1;

        return count < tokens.Count && tokens[count].Token is Number ? (tokens[count].Offset, tokens[count].Length) : null;
    }

    private static bool IsCountToken(Token token)
    {
        return token is Number or Word { QuoteStyle: null, Keyword: Keyword.ALL or Keyword.NULL };
    }

    private static bool HasOuterKeyword(string sql, string? dialect, Keyword keyword)
    {
        return OuterTokens(sql, dialect)?.Any(x => x.Depth == 0 && x.Token is Word { QuoteStyle: null } word && word.Keyword == keyword) == true;
    }

    // The SQL's tokens without whitespace and comments, each with its offset, its length in the text and its parenthesis
    // depth; null when a token cannot be located or its text does not match the SQL.
    private static List<OuterToken>? OuterTokens(string sql, string? dialect)
    {
        IList<Token> tokens;
        try
        {
            tokens = new Tokenizer().Tokenize(sql, SqlDialects.Resolve(dialect));
        }
        catch (Exception)
        {
            return null;
        }

        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\n')
            {
                lineStarts.Add(i + 1);
            }
        }

        var located = new List<OuterToken>();
        var depth = 0;
        foreach (var token in tokens)
        {
            if (token is Whitespace)
            {
                continue;
            }

            if (token is RightParen)
            {
                depth--;
            }

            var line = (int)token.Location.Line;
            var offset = line >= 1 && line <= lineStarts.Count ? lineStarts[line - 1] + (int)token.Location.Column - 1 : -1;
            var text = token switch
            {
                Number number => number.Value,
                Word { QuoteStyle: null } word => word.Value,
                _ => null
            };
            if (offset < 0 || (text != null && (offset + text.Length > sql.Length || !sql.AsSpan(offset, text.Length).Equals(text, StringComparison.OrdinalIgnoreCase))))
            {
                return null;
            }

            located.Add(new OuterToken(token, offset, text?.Length ?? 0, depth));

            if (token is LeftParen)
            {
                depth++;
            }
        }

        return located;
    }

    private sealed record OuterToken(Token Token, int Offset, int Length, int Depth);

    private enum OuterBound
    {
        None,
        WithinCap,
        Unbounded
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

        // A set operation (UNION / EXCEPT / INTERSECT) or a parenthesized query: a leading TOP would cap only the first arm,
        // and SQL Server accepts no ORDER BY item outside the select list after one, so ORDER BY (SELECT NULL) is refused.
        // The query is wrapped in a derived table capped by TOP instead — after its CTE list when it has one, since a CTE
        // cannot sit inside a derived table.
        if (query.Body is not SetExpression.SelectExpression
            && WrapTSqlBody(trimmed, query, maxRows, dialect) is { } wrapped)
        {
            return wrapped;
        }

        // A CTE-leading single SELECT: a leading TOP would land on the CTE body and leave the OUTER result uncapped, and
        // the statement cannot be wrapped in a derived table. Bound the outer result with a dummy-ordered OFFSET/FETCH (valid
        // T-SQL) instead.
        return AppendClause(trimmed, $"ORDER BY (SELECT NULL) OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY", dialect);
    }

    // The query body as a derived table capped by TOP — its CTE list, when it has one, kept in front — or null when the body
    // cannot be located in the text or the wrapped statement does not parse with a bound within the cap.
    private static string? WrapTSqlBody(string trimmed, Query query, int maxRows, string? dialect)
    {
        var bodyStart = query.With == null ? 0 : BodyStartAfterWith(trimmed, dialect);
        if (bodyStart < 0)
        {
            return null;
        }

        var wrapped = $"{trimmed[..bodyStart]}SELECT TOP {maxRows} * FROM (\n{trimmed[bodyStart..]}\n) AS beacon_rows";

        return IsBoundWithin(wrapped, maxRows, dialect) ? wrapped : null;
    }

    // The offset of the query body that follows the CTE list of a WITH clause (`WITH a [(cols)] AS (…) [, b AS (…)] <body>`),
    // or -1 when the tokens do not have that shape.
    private static int BodyStartAfterWith(string sql, string? dialect)
    {
        var tokens = OuterTokens(sql, dialect);
        var position = tokens?.FindIndex(x => x.Depth == 0 && x.Token is Word { QuoteStyle: null, Keyword: Keyword.WITH }) ?? -1;
        if (tokens == null || position < 0)
        {
            return -1;
        }

        position++;
        while (position < tokens.Count && tokens[position].Token is Word)
        {
            position++;
            if (position < tokens.Count && tokens[position].Token is LeftParen)
            {
                position = AfterClosingParen(tokens, position);
            }

            if (position < 0 || position >= tokens.Count || tokens[position].Token is not Word { QuoteStyle: null, Keyword: Keyword.AS })
            {
                return -1;
            }

            position++;
            if (position >= tokens.Count || tokens[position].Token is not LeftParen)
            {
                return -1;
            }

            position = AfterClosingParen(tokens, position);
            if (position < 0 || position >= tokens.Count)
            {
                return -1;
            }

            if (tokens[position].Token is not Comma)
            {
                var body = tokens[position];
                var located = body.Token is Word { QuoteStyle: null, Keyword: Keyword.SELECT }
                    || (body.Token is LeftParen && body.Offset < sql.Length && sql[body.Offset] == '(');

                return located ? body.Offset : -1;
            }

            position++;
        }

        return -1;
    }

    // The index after the parenthesis that closes the one at openAt, or -1 when it never closes.
    private static int AfterClosingParen(List<OuterToken> tokens, int openAt)
    {
        var depth = tokens[openAt].Depth;
        for (var i = openAt + 1; i < tokens.Count; i++)
        {
            if (tokens[i].Token is RightParen && tokens[i].Depth == depth)
            {
                return i + 1;
            }
        }

        return -1;
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
