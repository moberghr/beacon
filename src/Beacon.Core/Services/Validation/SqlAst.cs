using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;
using SqlParser.Tokens;

namespace Beacon.Core.Services.Validation;

/// <summary>
/// The one way Core parses SQL. The parser's recursion budget is pinned, and because that budget does not cover
/// left-associative chains (UNION arms, <c>a + b + c</c>, <c>x OR y OR z</c>) the parsed tree's depth is measured
/// without recursion before any recursive consumer — the validators, the library's visitor, <c>ToSql()</c> — walks it.
/// A deeper tree is refused: a stack overflow cannot be caught and would end the host process.
/// </summary>
internal static class SqlAst
{
    /// <summary>The parser's own budget for nesting it recurses on (parentheses, subqueries, unary operators).</summary>
    public const uint ParserRecursionLimit = 50;

    /// <summary>
    /// Deepest parsed tree, in node hops, any consumer may walk. Ordinary SQL stays under 100; 400 keeps the library's
    /// recursive visitor and <c>ToSql()</c> clear of the stack limit even on a 256 KB thread stack.
    /// </summary>
    public const int MaxDepth = 400;

    public static readonly string TooDeepMessage =
        "The SQL is nested too deeply to verify. Reduce nested parentheses and subqueries, or long UNION, AND/OR or arithmetic chains.";

    public static readonly string DisallowedCharacterMessage =
        "The SQL holds a control character or a Unicode noncharacter. Remove it: besides text, only tabs and line breaks are allowed.";

    public static readonly string UnmatchedEndMessage =
        "The SQL holds an END that closes no CASE, so it cannot be verified. Remove it, or quote the name END.";

    // The parser's own recursion budget reports exhaustion as an ordinary parse error with this text.
    private const string ParserRecursionLimitMessage = "Recursion limit exceeded";

    // A tree is far smaller than this for any SQL under the length cap; the bound only guarantees the walk ends.
    private const int MaxNodes = 2_000_000;

    private static readonly Assembly AstAssembly = typeof(Statement).Assembly;

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> ChildProperties = new();

    public static Sequence<Statement> Parse(string sql, string? dialect)
    {
        return Parse(sql, SqlDialects.Resolve(dialect));
    }

    /// <exception cref="TooDeepException">The parser ran out of recursion budget, or the parsed tree is deeper than
    /// <see cref="MaxDepth"/>.</exception>
    /// <exception cref="DisallowedCharacterException">The SQL holds a character <see cref="IndexOfDisallowedCharacter"/>
    /// finds.</exception>
    /// <exception cref="UnmatchedEndException">The SQL holds an unquoted END that closes no CASE.</exception>
    public static Sequence<Statement> Parse(string sql, Dialect dialect)
    {
        // The tokenizer reads U+FFFF as the end of its input: whatever follows would never be parsed, so every caller — the
        // read-only validator, the database-name rule, the row-cap and paging rewriters, the save checks — would judge a
        // prefix of the statement that runs.
        if (IndexOfDisallowedCharacter(sql) >= 0)
        {
            throw new DisallowedCharacterException();
        }

        Sequence<Statement> statements;
        try
        {
            statements = new Parser().ParseSql(sql, dialect, new ParserOptions { RecursionLimit = ParserRecursionLimit });
        }
        catch (ParserException ex) when (ex.Message.StartsWith(ParserRecursionLimitMessage, StringComparison.Ordinal))
        {
            throw new TooDeepException();
        }

        if (!IsWithinDepth(statements))
        {
            throw new TooDeepException();
        }

        // After a statement the parser reads END as the end of a block and stops, returning what it read so far: the text
        // after it would never be judged. In a query END only closes a CASE, so one more END than CASE is refused.
        if (sql.Contains("END", StringComparison.OrdinalIgnoreCase) && HasUnmatchedEnd(sql, dialect))
        {
            throw new UnmatchedEndException();
        }

        return statements;
    }

    /// <summary>
    /// The position of the first character no SQL may hold, or -1: a control character other than tab, LF and CR, and every
    /// Unicode noncharacter — U+FDD0 to U+FDEF and each code point ending in FFFE or FFFF, in every plane. The parser reads
    /// U+FFFF as the end of its input; the others have no place in SQL text, and an engine may read them differently from
    /// the parser. One linear pass.
    /// </summary>
    public static int IndexOfDisallowedCharacter(string? sql)
    {
        if (sql == null)
        {
            return -1;
        }

        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (char.IsControl(c) && c is not ('\t' or '\n' or '\r'))
            {
                return i;
            }

            if (c is >= '﷐' and <= '﷯' or '￾' or '￿')
            {
                return i;
            }

            // A noncharacter outside the basic plane is U+nFFFE or U+nFFFF, written as a surrogate pair.
            if (char.IsHighSurrogate(c)
                && i + 1 < sql.Length
                && char.IsLowSurrogate(sql[i + 1])
                && (char.ConvertToUtf32(c, sql[i + 1]) & 0xFFFE) == 0xFFFE)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The caller-facing refusal for a character <see cref="IndexOfDisallowedCharacter"/> found at <paramref name="index"/>.</summary>
    public static string DisallowedCharacterMessageAt(int index)
    {
        return $"The SQL holds a control character or a Unicode noncharacter at position {index + 1}. Remove it: besides text, only tabs and line breaks are allowed.";
    }

    /// <summary>
    /// Every node under <paramref name="root"/>, the root included, found by reflection over the node's public
    /// properties — so a node the library visitor skips (a derived table's subquery, a table function's arguments) is
    /// still seen. Iterative: callers walk trees <see cref="Parse(string, Dialect)"/> has already bounded.
    /// </summary>
    public static IEnumerable<object> Nodes(object root)
    {
        var pending = new Stack<object>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;

            foreach (var child in Children(node))
            {
                pending.Push(child);
            }
        }
    }

    private static bool HasUnmatchedEnd(string sql, Dialect dialect)
    {
        var cases = 0;
        var ends = 0;
        foreach (var token in new Tokenizer().Tokenize(sql, dialect))
        {
            if (token is not Word { QuoteStyle: null } word)
            {
                continue;
            }

            if (word.Keyword == Keyword.CASE)
            {
                cases++;
            }
            else if (word.Keyword == Keyword.END)
            {
                ends++;
            }
        }

        return ends > cases;
    }

    private static bool IsWithinDepth(object root)
    {
        var pending = new Stack<(object Node, int Depth)>();
        pending.Push((root, 0));
        var visited = 0;
        while (pending.Count > 0)
        {
            var (node, depth) = pending.Pop();
            if (depth > MaxDepth || ++visited > MaxNodes)
            {
                return false;
            }

            foreach (var child in Children(node))
            {
                pending.Push((child, depth + 1));
            }
        }

        return true;
    }

    private static IEnumerable<object> Children(object node)
    {
        if (node is IEnumerable items)
        {
            foreach (var item in items)
            {
                if (IsAstNode(item))
                {
                    yield return item;
                }
            }

            yield break;
        }

        foreach (var property in ChildProperties.GetOrAdd(node.GetType(), ReadableProperties))
        {
            var value = property.GetValue(node);
            if (IsAstNode(value))
            {
                yield return value;
            }
        }
    }

    private static PropertyInfo[] ReadableProperties(Type type)
    {
        return type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(x => x.GetIndexParameters().Length == 0)
            .Where(x => !x.PropertyType.IsPrimitive)
            .Where(x => !x.PropertyType.IsEnum)
            .Where(x => x.PropertyType != typeof(string))
            .ToArray();
    }

    private static bool IsAstNode([NotNullWhen(true)] object? value)
    {
        if (value == null || value is string)
        {
            return false;
        }

        var type = value.GetType();

        return !type.IsPrimitive
            && !type.IsEnum
            && (type.Assembly == AstAssembly || value is IEnumerable);
    }

    /// <summary>
    /// The SQL parsed, but its tree is too deep to walk safely. Unlike a parse error it is never a reason to fall back to
    /// a textual heuristic: the statement must not run.
    /// </summary>
    public sealed class TooDeepException() : UnverifiableSqlException(TooDeepMessage);

    /// <summary>
    /// The SQL holds a control character or a Unicode noncharacter (<see cref="IndexOfDisallowedCharacter"/>), which the
    /// parser cannot be trusted to read past.
    /// </summary>
    public sealed class DisallowedCharacterException() : UnverifiableSqlException(DisallowedCharacterMessage);

    /// <summary>The SQL holds an END that closes no CASE, where the parser stops reading.</summary>
    public sealed class UnmatchedEndException() : UnverifiableSqlException(UnmatchedEndMessage);

    /// <summary>
    /// The SQL parsed — or would have — but cannot be verified as a whole. Unlike a parse error it is never a reason to fall
    /// back to a textual heuristic: the statement must not run.
    /// </summary>
    public abstract class UnverifiableSqlException(string message) : InvalidOperationException(message);
}
