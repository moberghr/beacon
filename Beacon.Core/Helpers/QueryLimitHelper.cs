using System.Text.RegularExpressions;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Helpers;

/// <summary>
/// Helper class to add row limits to SQL queries based on database engine type.
/// Prevents OutOfMemoryException by limiting results at the database level.
/// </summary>
internal static class QueryLimitHelper
{
    /// <summary>
    /// Adds a LIMIT/TOP clause to a SQL query if one doesn't already exist.
    /// </summary>
    /// <param name="query">The SQL query to modify</param>
    /// <param name="databaseEngine">The target database engine</param>
    /// <param name="maxRows">Maximum number of rows to return</param>
    /// <returns>Modified query with limit clause, or original if limit already exists</returns>
    public static string AddLimitIfMissing(string query, DatabaseEngineType databaseEngine, int maxRows)
    {
        if (string.IsNullOrWhiteSpace(query))
            return query;

        var trimmedQuery = query.Trim();

        // Check if query already has a limit clause
        if (HasExistingLimit(trimmedQuery, databaseEngine))
            return query;

        return databaseEngine switch
        {
            DatabaseEngineType.PostgreSQL => AddPostgreSqlLimit(trimmedQuery, maxRows),
            DatabaseEngineType.MySQL => AddMySqlLimit(trimmedQuery, maxRows),
            DatabaseEngineType.MSSQL => AddSqlServerTop(trimmedQuery, maxRows),
            DatabaseEngineType.AzureSynapse => AddSqlServerTop(trimmedQuery, maxRows),
            DatabaseEngineType.SQLite => AddSqliteLimit(trimmedQuery, maxRows),
            DatabaseEngineType.Snowflake => AddPostgreSqlLimit(trimmedQuery, maxRows),
            _ => query // Unknown engine, return original
        };
    }

    private static bool HasExistingLimit(string query, DatabaseEngineType databaseEngine)
    {
        var upperQuery = query.ToUpperInvariant();

        return databaseEngine switch
        {
            DatabaseEngineType.PostgreSQL or DatabaseEngineType.MySQL or DatabaseEngineType.SQLite or DatabaseEngineType.Snowflake =>
                Regex.IsMatch(upperQuery, @"\bLIMIT\s+\d+", RegexOptions.IgnoreCase),

            DatabaseEngineType.MSSQL or DatabaseEngineType.AzureSynapse =>
                Regex.IsMatch(upperQuery, @"\bTOP\s+\d+", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(upperQuery, @"\bFETCH\s+(FIRST|NEXT)\s+\d+\s+ROWS?\s+ONLY", RegexOptions.IgnoreCase),

            _ => false
        };
    }

    private static string AddPostgreSqlLimit(string query, int maxRows)
    {
        // PostgreSQL: Add LIMIT at the end
        // Handle queries that end with semicolon
        if (query.TrimEnd().EndsWith(';'))
        {
            var withoutSemicolon = query.TrimEnd().TrimEnd(';');
            return $"{withoutSemicolon}\nLIMIT {maxRows};";
        }

        return $"{query}\nLIMIT {maxRows}";
    }

    private static string AddMySqlLimit(string query, int maxRows)
    {
        // MySQL uses same syntax as PostgreSQL
        return AddPostgreSqlLimit(query, maxRows);
    }

    private static string AddSqlServerTop(string query, int maxRows)
    {
        // SQL Server: Add TOP after SELECT keyword
        // Match SELECT with optional DISTINCT
        var pattern = @"^\s*SELECT\s+(DISTINCT\s+)?";
        var match = Regex.Match(query, pattern, RegexOptions.IgnoreCase);

        if (match.Success)
        {
            var distinctPart = match.Groups[1].Success ? match.Groups[1].Value : "";
            var replacement = $"SELECT {distinctPart}TOP {maxRows} ";
            return Regex.Replace(query, pattern, replacement, RegexOptions.IgnoreCase);
        }

        // If no SELECT found (e.g., CTE or complex query), try to add at the end using FETCH
        return AddSqlServerFetch(query, maxRows);
    }

    private static string AddSqlServerFetch(string query, int maxRows)
    {
        // SQL Server 2012+ : Use OFFSET/FETCH if there's an ORDER BY
        var upperQuery = query.ToUpperInvariant();
        if (upperQuery.Contains("ORDER BY"))
        {
            // Check if query ends with semicolon
            if (query.TrimEnd().EndsWith(';'))
            {
                var withoutSemicolon = query.TrimEnd().TrimEnd(';');
                return $"{withoutSemicolon}\nOFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY;";
            }

            return $"{query}\nOFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY";
        }

        // If no ORDER BY, just return original (TOP should have worked)
        return query;
    }

    private static string AddSqliteLimit(string query, int maxRows)
    {
        // SQLite uses same syntax as PostgreSQL/MySQL
        return AddPostgreSqlLimit(query, maxRows);
    }
}
