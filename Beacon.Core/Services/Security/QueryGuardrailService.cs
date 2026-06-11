using System.Text.RegularExpressions;

namespace Beacon.Core.Services.Security;

internal sealed class QueryGuardrailService : IQueryGuardrailService
{
    // SQL keywords that indicate write operations
    private static readonly Regex WriteOperationPattern = new(
        @"\b(INSERT\s+INTO|UPDATE\s+\w|DELETE\s+FROM|DROP\s+|ALTER\s+|TRUNCATE\s+|CREATE\s+|GRANT\s+|REVOKE\s+|EXEC\s+|EXECUTE\s+|MERGE\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Common PII column name patterns
    private static readonly Regex PiiColumnPattern = new(
        @"(email|e_mail|phone|telephone|mobile|ssn|social_security|tax_id|passport|credit_card|card_number|cvv|password|pwd|secret|token|birth_?date|dob|address|zip_?code|postal|ip_address|national_id)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Dangerous patterns (comments hiding write ops, stacked queries)
    private static readonly Regex DangerousPattern = new(
        @"(;\s*(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|EXEC))|(/\*.*?(INSERT|UPDATE|DELETE|DROP).*?\*/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline);

    public QueryValidationResult ValidateQuery(string sql, QueryGuardrailOptions? options = null)
    {
        options ??= new QueryGuardrailOptions();

        if (string.IsNullOrWhiteSpace(sql))
            return new QueryValidationResult(false, "Query cannot be empty");

        var trimmedSql = sql.Trim();

        if (options.ReadOnly)
        {
            // Check for write operations
            if (WriteOperationPattern.IsMatch(trimmedSql))
                return new QueryValidationResult(false, "Write operations are not allowed. Only SELECT queries are permitted.", false);

            // Check for dangerous patterns (stacked queries with writes, comments hiding writes)
            if (DangerousPattern.IsMatch(trimmedSql))
                return new QueryValidationResult(false, "Query contains potentially dangerous patterns.", false);

            // Must start with SELECT, WITH, or EXPLAIN
            if (!Regex.IsMatch(trimmedSql, @"^\s*(SELECT|WITH|EXPLAIN)\s", RegexOptions.IgnoreCase))
                return new QueryValidationResult(false, "Query must start with SELECT, WITH, or EXPLAIN.", false);
        }

        // Detect PII columns in the query
        List<string>? piiColumns = null;
        if (options.DetectPii)
        {
            var matches = PiiColumnPattern.Matches(trimmedSql);
            if (matches.Count > 0)
                piiColumns = matches.Select(m => m.Value).Distinct().ToList();

            // Also match against custom PII patterns
            if (options.CustomPiiPatterns is { Count: > 0 })
            {
                piiColumns ??= [];
                foreach (var pattern in options.CustomPiiPatterns)
                {
                    try
                    {
                        var customMatches = Regex.Matches(trimmedSql, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
                        foreach (Match m in customMatches)
                        {
                            if (!piiColumns.Contains(m.Value, StringComparer.OrdinalIgnoreCase))
                                piiColumns.Add(m.Value);
                        }
                    }
                    catch
                    {
                        // Invalid regex pattern — skip
                    }
                }
            }
        }

        return new QueryValidationResult(true, PiiColumns: piiColumns);
    }

    public string ApplyRowLimit(string sql, int maxRows, string? databaseEngine = null)
    {
        if (maxRows <= 0) return sql;

        var trimmed = sql.TrimEnd().TrimEnd(';');

        // Check if query already has a LIMIT/TOP clause
        if (Regex.IsMatch(trimmed, @"\bLIMIT\s+\d+", RegexOptions.IgnoreCase))
            return sql;
        if (Regex.IsMatch(trimmed, @"\bTOP\s+\d+", RegexOptions.IgnoreCase))
            return sql;

        // Apply engine-specific row limit
        if (string.Equals(databaseEngine, "MSSQL", StringComparison.OrdinalIgnoreCase))
        {
            // For SQL Server, wrap in a subquery with TOP if no ORDER BY, or use TOP directly
            if (Regex.IsMatch(trimmed, @"\bORDER\s+BY\b", RegexOptions.IgnoreCase))
                return $"{trimmed} OFFSET 0 ROWS FETCH NEXT {maxRows} ROWS ONLY";

            // Insert TOP after SELECT
            return Regex.Replace(trimmed, @"\bSELECT\b", $"SELECT TOP {maxRows}", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        }

        // PostgreSQL / MySQL - use LIMIT
        return $"{trimmed} LIMIT {maxRows}";
    }

    public List<string> DetectPiiColumns(string sql, IEnumerable<string> columnNames)
    {
        return columnNames
            .Where(col => PiiColumnPattern.IsMatch(col))
            .ToList();
    }

    public bool IsPiiColumn(string columnName, IReadOnlyList<string>? customPatterns = null)
    {
        if (PiiColumnPattern.IsMatch(columnName))
        {
            return true;
        }

        if (customPatterns is not { Count: > 0 })
        {
            return false;
        }

        foreach (var pattern in customPatterns)
        {
            try
            {
                if (Regex.IsMatch(columnName, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
                {
                    return true;
                }
            }
            catch
            {
                // Invalid regex pattern — skip
            }
        }

        return false;
    }

    public Dictionary<string, object?> MaskPiiValues(Dictionary<string, object?> row, IEnumerable<string> piiColumns)
    {
        var piiSet = new HashSet<string>(piiColumns, StringComparer.OrdinalIgnoreCase);
        var masked = new Dictionary<string, object?>(row);

        foreach (var key in masked.Keys.ToList())
        {
            if (piiSet.Contains(key) && masked[key] != null)
            {
                var value = masked[key]?.ToString() ?? "";
                masked[key] = value.Length <= 2 ? "***" : $"{value[..1]}***{value[^1..]}";
            }
        }

        return masked;
    }
}
