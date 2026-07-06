using Beacon.Core.Models.Subscriptions;

namespace Beacon.Core.Helpers;

/// <summary>
/// Helper class for SQL query compilation and parameter handling.
/// Uses proper parameterized queries to prevent SQL injection attacks.
/// </summary>
internal class QueryHelper
{
    /// <summary>
    /// Compiles a SQL query by converting custom placeholders to database-compatible parameter names.
    /// Returns both the modified SQL and a dictionary of parameter values for use with parameterized queries.
    /// </summary>
    /// <param name="querySql">The SQL query with custom placeholders (e.g., {{userId}})</param>
    /// <param name="parameterValues">List of parameter values to substitute</param>
    /// <returns>Tuple containing the SQL with @param placeholders and a dictionary of parameter values</returns>
    public static (string Sql, Dictionary<string, object?> Parameters) PrepareParameterizedQuery(
        string querySql,
        List<SubscriptionParameterData>? parameterValues)
    {
        var parameters = new Dictionary<string, object?>();

        if (parameterValues == null || parameterValues.Count == 0)
        {
            return (querySql, parameters);
        }

        var parameterIndex = 0;
        foreach (var parameter in parameterValues)
        {
            if (string.IsNullOrEmpty(parameter.QueryPlaceholder))
            {
                continue;
            }

            // Generate a safe parameter name (e.g., @p0, @p1, etc.)
            // This ensures compatibility across SQL Server, PostgreSQL, and MySQL
            var safeParamName = $"@p{parameterIndex}";

            // Replace the custom placeholder with the database parameter placeholder
            querySql = querySql.Replace(parameter.QueryPlaceholder, safeParamName);

            // Add to parameters dictionary with the parameter name (without @)
            // Dapper expects parameter names without the @ prefix in the dictionary
            parameters.Add($"p{parameterIndex}", parameter.Value);

            parameterIndex++;
        }

        return (querySql, parameters);
    }

    /// <summary>
    /// Legacy method for backward compatibility.
    /// DEPRECATED: This method uses string replacement and should be replaced with PrepareParameterizedQuery.
    /// </summary>
    [Obsolete("Use PrepareParameterizedQuery instead for better SQL injection protection")]
    public static string CompileSql(string querySql, List<SubscriptionParameterData>? parameterValues)
    {
        // For backward compatibility, just return the SQL part of the new method
        var (sql, _) = PrepareParameterizedQuery(querySql, parameterValues);
        return sql;
    }
}
