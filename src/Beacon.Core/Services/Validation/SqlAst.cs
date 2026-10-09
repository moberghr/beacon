using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using SqlParser;
using SqlParser.Ast;
using SqlParser.Dialects;

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
    public static Sequence<Statement> Parse(string sql, Dialect dialect)
    {
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

        return statements;
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
    public sealed class TooDeepException() : InvalidOperationException(TooDeepMessage);
}
