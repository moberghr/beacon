using System.Text.RegularExpressions;
using Semantico.Core.Models;
using Semantico.Core.Models.Queries;

namespace Semantico.Core.Validators;

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
    /// <exception cref="SemanticoException">Thrown when blocked keywords are found</exception>
    public static void CheckForFlaggedWords(string sqlQuery)
    {
        if (string.IsNullOrWhiteSpace(sqlQuery))
        {
            throw new SemanticoException("SQL query cannot be empty.");
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
            throw new SemanticoException(
                $"Query contains blocked SQL keywords: {string.Join(", ", foundBlockedKeywords)}. " +
                "Only SELECT queries are allowed.");
        }
    }

    /// <summary>
    /// Validates that all defined parameters are present in the query.
    /// </summary>
    /// <param name="sqlQuery">The SQL query to validate</param>
    /// <param name="parameters">The list of required parameters</param>
    /// <exception cref="SemanticoException">Thrown when a parameter is missing</exception>
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
            throw new SemanticoException(
                $"Query is missing required parameters: {string.Join(", ", missingParameters)}");
        }
    }

    /// <summary>
    /// Removes SQL comments (single-line and multi-line) from the query.
    /// </summary>
    private static string RemoveSqlComments(string sql)
    {
        // Remove multi-line comments /* ... */
        sql = Regex.Replace(sql, @"/\*.*?\*/", " ", RegexOptions.Singleline);

        // Remove single-line comments -- ...
        sql = Regex.Replace(sql, @"--.*?$", " ", RegexOptions.Multiline);

        return sql;
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
