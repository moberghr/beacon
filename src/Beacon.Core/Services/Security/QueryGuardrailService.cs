using System.Text.RegularExpressions;
using Beacon.Core.Services.Validation;

namespace Beacon.Core.Services.Security;

internal sealed class QueryGuardrailService : IQueryGuardrailService
{
    // Declared first: static fields initialise in textual order and every pattern below is built with it.
    // Each pattern is linear on its own; the timeout is the backstop, and IsMatchFailClosed reads it as a match.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    // SQL keywords that indicate write operations
    private static readonly Regex WriteOperationPattern = new(
        @"\b(INSERT\s+INTO|UPDATE\s+\w|DELETE\s+FROM|DROP\s+|ALTER\s+|TRUNCATE\s+|CREATE\s+|GRANT\s+|REVOKE\s+|EXEC\s+|EXECUTE\s+|MERGE\s+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    // Common PII column name patterns
    private static readonly Regex PiiColumnPattern = new(
        @"(email|e_mail|phone|telephone|mobile|ssn|social_security|tax_id|passport|credit_card|card_number|cvv|password|pwd|secret|token|birth_?date|dob|address|zip_?code|postal|ip_address|national_id)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    // Stacked statement whose second statement writes.
    private static readonly Regex DangerousPattern = new(
        @";\s*(INSERT|UPDATE|DELETE|DROP|ALTER|TRUNCATE|CREATE|EXEC)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    // A `/*` followed — anywhere later — by a write keyword and then by a `*/`. Same language as the former
    // `/\*.*?(INSERT|UPDATE|DELETE|DROP).*?\*/` alternative, which backtracked super-linearly (16 KB → 1.7 s):
    // anchored at \A, each atomic lazy step commits to the EARLIEST occurrence, which is the one most likely to
    // complete the match (the keywords cannot overlap one another), so the scan is one linear pass. Kept rather
    // than dropped because it is not redundant: it rejects write keywords WriteOperationPattern cannot see, such
    // as a MySQL executable comment `/*!50000DELETE*/ FROM t`.
    private static readonly Regex CommentHiddenWritePattern = new(
        @"\A(?>.*?/\*)(?>.*?(?:INSERT|UPDATE|DELETE|DROP))(?>.*?\*/)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline,
        MatchTimeout);

    // Matches when the query does NOT start with SELECT, WITH or EXPLAIN — phrased as a rejection so it goes
    // through IsMatchFailClosed like every other check (a timeout rejects rather than admits).
    private static readonly Regex NonReadOnlyPrefixPattern = new(
        @"^(?!\s*(SELECT|WITH|EXPLAIN)\s)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled,
        MatchTimeout);

    public QueryValidationResult ValidateQuery(string sql, QueryGuardrailOptions? options = null)
    {
        options ??= new QueryGuardrailOptions();

        if (string.IsNullOrWhiteSpace(sql))
            return new QueryValidationResult(false, "Query cannot be empty");

        var trimmedSql = sql.Trim();

        if (options.ReadOnly)
        {
            // Check for write operations
            if (IsMatchFailClosed(WriteOperationPattern, trimmedSql))
            {
                return new QueryValidationResult(false, "Write operations are not allowed. Only SELECT queries are permitted.", false);
            }

            // Check for dangerous patterns (stacked queries with writes, comments hiding writes)
            if (IsMatchFailClosed(DangerousPattern, trimmedSql) || IsMatchFailClosed(CommentHiddenWritePattern, trimmedSql))
            {
                return new QueryValidationResult(false, "Query contains potentially dangerous patterns.", false);
            }

            // Must start with SELECT, WITH, or EXPLAIN
            if (IsMatchFailClosed(NonReadOnlyPrefixPattern, trimmedSql))
            {
                return new QueryValidationResult(false, "Query must start with SELECT, WITH, or EXPLAIN.", false);
            }
        }

        // Detect PII columns in the query. Not a yes/no check, so it cannot go through IsMatchFailClosed: a
        // timeout here propagates and aborts the call, which runs nothing.
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
        // Placement is decided on the parsed AST (SqlRowLimitRewriter) so a LIMIT/TOP inside a string
        // literal or a subquery no longer reads as "already bounded" and leaves the outer result uncapped.
        return SqlRowLimitRewriter.Apply(sql, maxRows, databaseEngine).Sql;
    }

    public List<string> DetectPiiColumns(string sql, IEnumerable<string> columnNames)
    {
        return columnNames
            .Where(x => IsMatchFailClosed(PiiColumnPattern, x))
            .ToList();
    }

    public bool IsPiiColumn(string columnName, IReadOnlyList<string>? customPatterns = null)
    {
        if (IsMatchFailClosed(PiiColumnPattern, columnName))
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
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                // Fail CLOSED: if a custom pattern can't be evaluated, treat the column as PII rather
                // than risk leaking it. Patterns are validated at write time, so this is defensive.
                return true;
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

    // The one seam every guardrail match goes through (§1.5 fail closed): a pattern that cannot finish within its
    // timeout counts as a match — the dangerous / PII answer — never as a pass. Internal for unit tests.
    internal static bool IsMatchFailClosed(Regex pattern, string input)
    {
        try
        {
            return pattern.IsMatch(input);
        }
        catch (RegexMatchTimeoutException)
        {
            return true;
        }
    }
}
