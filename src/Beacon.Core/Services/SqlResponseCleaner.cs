namespace Beacon.Core.Services;

/// <summary>
/// Pulls the SQL out of a model response: the body of the first code fence, or from the first SELECT, WITH or EXPLAIN
/// when there is no fence, cut after the last semicolon.
/// </summary>
internal static class SqlResponseCleaner
{
    private static readonly string[] Keywords = ["SELECT ", "WITH ", "EXPLAIN "];

    /// <param name="content">The model's response.</param>
    /// <param name="foundSql">False when the response has neither a code fence nor a SELECT, WITH or EXPLAIN keyword, so what comes back is only the trimmed text.</param>
    public static string Clean(string content, out bool foundSql)
    {
        var sql = content.Trim();
        foundSql = true;

        if (sql.Contains("```"))
        {
            var startIdx = sql.IndexOf("```", StringComparison.Ordinal);
            var afterFence = sql[(startIdx + 3)..];
            var newlineIdx = afterFence.IndexOf('\n');
            if (newlineIdx >= 0)
            {
                afterFence = afterFence[(newlineIdx + 1)..];
            }

            var endIdx = afterFence.IndexOf("```", StringComparison.Ordinal);
            sql = endIdx >= 0 ? afterFence[..endIdx].Trim() : afterFence.Trim();
        }
        else
        {
            var bestIdx = -1;
            foreach (var keyword in Keywords)
            {
                var idx = sql.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestIdx < 0 || idx < bestIdx))
                {
                    bestIdx = idx;
                }
            }

            foundSql = bestIdx >= 0;
            if (bestIdx > 0)
            {
                sql = sql[bestIdx..].Trim();
            }
        }

        var lastSemicolon = sql.LastIndexOf(';');
        if (lastSemicolon >= 0)
        {
            sql = sql[..(lastSemicolon + 1)].Trim();
        }

        return sql;
    }
}
