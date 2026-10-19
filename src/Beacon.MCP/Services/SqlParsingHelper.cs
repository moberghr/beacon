namespace Beacon.MCP.Services;

internal static class SqlParsingHelper
{
    internal static string CleanSqlResponse(string content)
    {
        var sql = content.Trim();

        if (sql.Contains("```"))
        {
            var startIdx = sql.IndexOf("```", StringComparison.Ordinal);
            var afterFence = sql[(startIdx + 3)..];
            var newlineIdx = afterFence.IndexOf('\n');
            if (newlineIdx >= 0)
                afterFence = afterFence[(newlineIdx + 1)..];
            var endIdx = afterFence.IndexOf("```", StringComparison.Ordinal);
            sql = endIdx >= 0 ? afterFence[..endIdx].Trim() : afterFence.Trim();
        }
        else
        {
            var keywords = new[] { "SELECT ", "WITH ", "EXPLAIN " };
            var bestIdx = -1;
            foreach (var kw in keywords)
            {
                var idx = sql.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                if (idx >= 0 && (bestIdx < 0 || idx < bestIdx))
                    bestIdx = idx;
            }

            if (bestIdx > 0)
                sql = sql[bestIdx..].Trim();
        }

        var lastSemicolon = sql.LastIndexOf(';');
        if (lastSemicolon >= 0)
            sql = sql[..(lastSemicolon + 1)].Trim();

        return sql;
    }
}
