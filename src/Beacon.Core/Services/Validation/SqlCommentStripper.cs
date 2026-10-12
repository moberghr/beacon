using SqlParser;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// Removes the comments from SQL before the regex guardrail judges it, so a saved query that starts with
/// <c>-- Revenue by month</c> or explains itself with <c>/* delete old rows first */</c> is judged by its statement, not
/// by its prose. Comments are found by the dialect's tokenizer — the one the AST stage parses with — and each is replaced
/// by a space; MySQL executable comments and optimizer hints (<c>/*!…*/</c>, <c>/*+…*/</c>) are code, so they stay.
/// Fail closed: the text comes back unchanged (and is judged with its comments) when the dialect is unknown, the SQL does
/// not tokenize, the SQL holds a control character or a Unicode noncharacter (the tokenizer reads U+FFFF as the end of its
/// input, so its tokens would not cover the text), the SQL relies on a lexical rule the engine reads differently from the
/// parser (the AST stage refuses it), a block comment holds another <c>/*</c> on an engine that does not nest them, or a
/// comment's own text — its marker and value, measured from where it starts — does not match the SQL, does not end where
/// the engine ends it, or is not followed directly by the next token or the end of the input.
/// The AST stage is not affected: it always parses the SQL as written.
/// </summary>
internal static class SqlCommentStripper
{
    /// <summary>The SQL with its line and block comments replaced by a space.</summary>
    public static string Strip(string sql, string? dialect)
    {
        return Strip(sql, dialect, lineCommentsOnly: false);
    }

    /// <summary>
    /// The SQL with its line comments (<c>--</c>, and <c>#</c> / <c>//</c> where the dialect has them) replaced by a space,
    /// so the text can be put on one line without a comment swallowing the rest of the statement.
    /// </summary>
    public static string StripLineComments(string sql, string? dialect)
    {
        return Strip(sql, dialect, lineCommentsOnly: true);
    }

    private static string Strip(string sql, string? dialect, bool lineCommentsOnly)
    {
        if (string.IsNullOrEmpty(sql) || !MayHoldComment(sql, dialect) || SqlAst.IndexOfDisallowedCharacter(sql) >= 0)
        {
            return sql;
        }

        var parserDialect = SqlDialects.Resolve(dialect);
        if (parserDialect is GenericDialect || SqlReadOnlyAstValidator.RejectLexicalDifferences(sql, dialect, parserDialect) != null)
        {
            return sql;
        }

        IList<Token> tokens;
        try
        {
            tokens = new Tokenizer().Tokenize(sql, parserDialect);
        }
        catch (Exception)
        {
            // Not tokenizable: the AST stage refuses it; the regex stage judges the text as written.
            return sql;
        }

        var lineStarts = LineStarts(sql);
        var spans = new List<(int Start, int End)>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i] is not Whitespace { WhitespaceKind: WhitespaceKind.InlineComment or WhitespaceKind.MultilineComment } comment)
            {
                continue;
            }

            // The comment's end is computed from its own text, never taken from the next token or the end of the input, and
            // that end must be exactly where the next token starts — or the end of the input for the last token. A comment
            // whose text does not match the SQL, or that the engine would end elsewhere, leaves the SQL as written.
            var start = OffsetOf(tokens[i].Location, lineStarts, sql.Length);
            var text = comment.ToString();
            var end = start + text.Length;
            var next = i + 1 < tokens.Count ? OffsetOf(tokens[i + 1].Location, lineStarts, sql.Length) : sql.Length;
            if (start < 0
                || end > sql.Length
                || end != next
                || !StartsComment(sql, start)
                || !sql.AsSpan(start, text.Length).SequenceEqual(text)
                || !EndsWhereTheEngineEndsIt(sql, start, end, comment.WhitespaceKind, dialect))
            {
                return sql;
            }

            if (IsExecutableComment(sql, start) || (lineCommentsOnly && comment.WhitespaceKind == WhitespaceKind.MultilineComment))
            {
                continue;
            }

            if (comment.WhitespaceKind == WhitespaceKind.MultilineComment
                && !SqlDialects.NestsBlockComments(dialect)
                && (comment.Value ?? "").Contains("/*", StringComparison.Ordinal))
            {
                return sql;
            }

            spans.Add((start, end));
        }

        if (spans.Count == 0)
        {
            return sql;
        }

        var builder = new System.Text.StringBuilder(sql.Length);
        var position = 0;
        foreach (var (start, end) in spans)
        {
            builder.Append(sql, position, start - position);
            builder.Append(' ');
            position = end;
        }

        builder.Append(sql, position, sql.Length - position);

        return builder.ToString();
    }

    // A line comment runs to its first line break, which it keeps, or to the end of the input. A block comment ends at the
    // first `*/` after its opening `/*` on an engine that does not nest comments, and where its nesting depth returns to
    // zero on one that does — each marker read as two characters of its own, as PostgreSQL reads them.
    private static bool EndsWhereTheEngineEndsIt(string sql, int start, int end, WhitespaceKind kind, string? dialect)
    {
        if (kind == WhitespaceKind.InlineComment)
        {
            var lineBreak = sql.IndexOf('\n', start, end - start);

            return lineBreak < 0 ? end == sql.Length : lineBreak == end - 1;
        }

        if (!sql.AsSpan(start).StartsWith("/*"))
        {
            return false;
        }

        if (!SqlDialects.NestsBlockComments(dialect))
        {
            return sql.IndexOf("*/", start + 2, StringComparison.Ordinal) == end - 2;
        }

        var depth = 1;
        var position = start + 2;
        while (position + 1 < end)
        {
            if (sql[position] == '/' && sql[position + 1] == '*')
            {
                depth++;
                position += 2;
            }
            else if (sql[position] == '*' && sql[position + 1] == '/')
            {
                depth--;
                position += 2;
                if (depth == 0)
                {
                    return position == end;
                }
            }
            else
            {
                position++;
            }
        }

        return false;
    }

    private static bool MayHoldComment(string sql, string? dialect)
    {
        return sql.Contains("/*", StringComparison.Ordinal) || SqlDialects.HasLineCommentMarker(sql, dialect);
    }

    private static bool StartsComment(string sql, int offset)
    {
        var rest = sql.AsSpan(offset);

        return rest.StartsWith("--") || rest.StartsWith("/*") || rest.StartsWith("//") || rest.StartsWith("#");
    }

    private static bool IsExecutableComment(string sql, int offset)
    {
        var rest = sql.AsSpan(offset);

        return rest.StartsWith("/*!") || rest.StartsWith("/*+");
    }

    private static int OffsetOf(Location location, List<int> lineStarts, int length)
    {
        var line = location.Line;
        if (line < 1 || line > lineStarts.Count)
        {
            return -1;
        }

        var offset = lineStarts[(int)line - 1] + (int)location.Column - 1;

        return offset >= 0 && offset <= length ? offset : -1;
    }

    private static List<int> LineStarts(string sql)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < sql.Length; i++)
        {
            if (sql[i] == '\n')
            {
                starts.Add(i + 1);
            }
        }

        return starts;
    }
}
