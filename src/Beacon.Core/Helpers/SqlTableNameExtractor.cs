using System.Text.RegularExpressions;

namespace Beacon.Core.Helpers;

/// <summary>
/// Extracts table names (optionally schema-qualified) referenced in a SQL statement via a
/// FROM/JOIN regex scan. Shared by MCP and AI SQL-generation paths so the parsing logic lives
/// in one place rather than being duplicated per project.
/// </summary>
public static partial class SqlTableNameExtractor
{
    [GeneratedRegex(@"(?:FROM|JOIN)\s+(?:""?(\w+)""?\.)?""?(\w+)""?", RegexOptions.IgnoreCase)]
    private static partial Regex TableNameRegex();

    public static List<string> ExtractTableNames(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in TableNameRegex().Matches(sql))
        {
            var schema = match.Groups[1].Value;
            var table = match.Groups[2].Value;
            if (table.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
                table.Equals("LATERAL", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            tables.Add(string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}");
        }

        return [.. tables];
    }
}
