using System.Text;
using System.Text.RegularExpressions;
using Beacon.Core.Configuration;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;

namespace Beacon.Core.Validators;

internal static class QueryValidator
{
    /// <summary>
    /// SQL keywords that are blocked to prevent data modification queries.
    /// These keywords indicate write operations that should not be allowed in read-only query execution.
    /// </summary>
    private static readonly HashSet<string> BlockedSqlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "INSERT",
        "UPDATE",
        "DELETE",
        "DROP",
        "REPLACE",
        "ALTER",
        "TRUNCATE",
        "CREATE",
        "EXEC",
        "EXECUTE"
    };

    /// <summary>
    /// Validates that the query does not contain blocked SQL keywords.
    /// </summary>
    /// <param name="sqlQuery">The SQL query to validate</param>
    /// <param name="maxChars">Longest SQL accepted, normally the read-only validator's <c>MaxSqlChars</c>.</param>
    /// <exception cref="BeaconException">Thrown when blocked keywords are found or the SQL is too long</exception>
    public static void CheckForFlaggedWords(string sqlQuery, int maxChars = McpCeilingOptions.DefaultMaxSqlChars)
    {
        if (string.IsNullOrWhiteSpace(sqlQuery))
        {
            throw new BeaconException("SQL query cannot be empty.");
        }

        if (sqlQuery.Length > maxChars)
        {
            throw new BeaconException($"SQL is {sqlQuery.Length} characters long; the limit is {maxChars}.");
        }

        // Remove SQL comments to prevent comment-based bypasses
        var cleanedQuery = RemoveSqlComments(sqlQuery);

        // Remove string literals to prevent bypasses like: SELECT 'INSERT' FROM table
        cleanedQuery = RemoveSqlStringLiterals(cleanedQuery);

        // Extract words from the cleaned query
        var words = Regex.Split(cleanedQuery, @"\W+")
            .Where(w => !string.IsNullOrWhiteSpace(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Check for blocked keywords
        var foundBlockedKeywords = words.Intersect(BlockedSqlKeywords, StringComparer.OrdinalIgnoreCase).ToList();

        if (foundBlockedKeywords.Any())
        {
            throw new BeaconException(
                $"Query contains blocked SQL keywords: {string.Join(", ", foundBlockedKeywords)}. " +
                "Only SELECT queries are allowed.");
        }
    }

    /// <summary>
    /// Validates that all defined parameters are present in the query.
    /// </summary>
    /// <param name="sqlQuery">The SQL query to validate</param>
    /// <param name="parameters">The list of required parameters</param>
    /// <exception cref="BeaconException">Thrown when a parameter is missing</exception>
    public static void CheckForParameters(string sqlQuery, List<QueryParameterData> parameters)
    {
        if (parameters == null || parameters.Count == 0)
        {
            return;
        }

        var missingParameters = parameters
            .Where(p => !sqlQuery.Contains(p.Placeholder, StringComparison.Ordinal))
            .Select(p => p.Placeholder)
            .ToList();

        if (missingParameters.Any())
        {
            throw new BeaconException(
                $"Query is missing required parameters: {string.Join(", ", missingParameters)}");
        }
    }

    /// <summary>
    /// Removes SQL comments (single-line and multi-line) from the query in one forward pass each, so the cost stays
    /// linear in the SQL length whatever the comment markers.
    /// </summary>
    private static string RemoveSqlComments(string sql)
    {
        return RemoveLineComments(RemoveBlockComments(sql));
    }

    // `/* ... */` up to the first `*/`, as engines that do not nest comments read it; an unclosed `/*` and everything
    // after it stay. A MySQL executable comment (`/*! ... */`, MariaDB `/*M! ... */`) keeps its body: MySQL runs it.
    private static string RemoveBlockComments(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var position = 0;
        while (position < sql.Length)
        {
            var open = sql.IndexOf("/*", position, StringComparison.Ordinal);
            var close = open < 0 ? -1 : sql.IndexOf("*/", open + 2, StringComparison.Ordinal);
            if (close < 0)
            {
                break;
            }

            result.Append(sql, position, open - position).Append(' ');
            var body = ExecutableCommentBodyStart(sql, open + 2);
            if (body >= 0 && body <= close)
            {
                result.Append(sql, body, close - body).Append(' ');
            }

            position = close + 2;
        }

        return result.Append(sql, position, sql.Length - position).ToString();
    }

    // `-- ...` up to, not including, the next CR or LF: PostgreSQL and others end a line comment at either.
    private static string RemoveLineComments(string sql)
    {
        var result = new StringBuilder(sql.Length);
        var position = 0;
        while (position < sql.Length)
        {
            var dash = sql.IndexOf("--", position, StringComparison.Ordinal);
            if (dash < 0)
            {
                break;
            }

            result.Append(sql, position, dash - position).Append(' ');
            var lineEnd = sql.IndexOfAny(['\r', '\n'], dash + 2);
            position = lineEnd < 0 ? sql.Length : lineEnd;
        }

        return result.Append(sql, position, sql.Length - position).ToString();
    }

    private static int ExecutableCommentBodyStart(string sql, int afterOpener)
    {
        var rest = sql.AsSpan(afterOpener);
        if (rest.StartsWith("!"))
        {
            return afterOpener + 1;
        }

        return rest.StartsWith("M!", StringComparison.OrdinalIgnoreCase) ? afterOpener + 2 : -1;
    }

    /// <summary>
    /// Removes SQL string literals ('...' or "...") from the query.
    /// </summary>
    private static string RemoveSqlStringLiterals(string sql)
    {
        // Remove single-quoted strings
        sql = Regex.Replace(sql, @"'([^']|'')*'", " ");

        // Remove double-quoted strings (used in some SQL dialects for identifiers)
        sql = Regex.Replace(sql, @"""([^""]|"""")*""", " ");

        return sql;
    }
}
