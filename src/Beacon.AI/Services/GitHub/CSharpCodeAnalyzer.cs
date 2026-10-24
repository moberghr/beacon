using System.Text.RegularExpressions;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.GitHub;

internal sealed class CSharpCodeAnalyzer : ICodeAnalyzer
{
    public string Language => "C#";

    // A pattern that backtracks on hostile input stops with RegexMatchTimeoutException instead of holding a thread.
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(2);

    private static readonly string[] CSharpExtensions = { ".cs" };

    public bool CanAnalyze(string filePath) =>
        CSharpExtensions.Any(ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public List<CodeReference> AnalyzeFile(string filePath, string content)
    {
        var references = new List<CodeReference>();
        var lineEnds = BuildLineEnds(content);
        var className = ExtractClassName(content);
        var methodNames = new Lazy<List<(int End, string Name)>>(() => FindMethodNames(content));

        // Detect EF Core DbSet<T> declarations
        var dbSetMatches = Regex.Matches(content, @"DbSet<(\w+)>\s+(\w+)", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in dbSetMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lineEnds, match.Index),
                ReferenceType = CodeReferenceType.EntityModel,
                TableName = match.Groups[1].Value,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect ToTable("tablename") or ToTable("tablename", "schema")
        var toTableMatches = Regex.Matches(content, @"\.ToTable\(\s*""([^""]+)""(?:\s*,\s*""([^""]+)"")?\s*\)", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in toTableMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lineEnds, match.Index),
                ReferenceType = CodeReferenceType.DbContextConfiguration,
                TableName = match.Groups[1].Value,
                SchemaName = match.Groups[2].Success ? match.Groups[2].Value : null,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect [Table("name")] attribute
        var tableAttrMatches = Regex.Matches(content, @"\[Table\(\s*""([^""]+)""(?:\s*,\s*Schema\s*=\s*""([^""]+)"")?\s*\)\]", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in tableAttrMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lineEnds, match.Index),
                ReferenceType = CodeReferenceType.EntityModel,
                TableName = match.Groups[1].Value,
                SchemaName = match.Groups[2].Success ? match.Groups[2].Value : null,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect Dapper queries: connection.Query/Execute/QueryAsync/ExecuteAsync
        var dapperMatches = Regex.Matches(content, @"\.(Query|Execute|QueryAsync|ExecuteAsync|QueryFirst|QuerySingle|QueryFirstOrDefault|QuerySingleOrDefault)\w*(?:<[^>]+>)?\s*\(\s*(?:""([^""]*)""|@""([^""]*)""|(\$""[^""]*""))", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in dapperMatches)
        {
            var sql = match.Groups[2].Success ? match.Groups[2].Value :
                      match.Groups[3].Success ? match.Groups[3].Value :
                      match.Groups[4].Value;
            var tables = ExtractTableNamesFromSql(sql);
            var method = ExtractMethodName(methodNames.Value, match.Index);
            foreach (var table in tables)
            {
                references.Add(new CodeReference
                {
                    FilePath = filePath,
                    LineNumber = GetLineNumber(lineEnds, match.Index),
                    ReferenceType = CodeReferenceType.DapperQuery,
                    TableName = table.Table,
                    SchemaName = table.Schema,
                    ClassName = className,
                    MethodName = method,
                    CodeSnippet = TruncateSnippet(match.Value, 500)
                });
            }
        }

        // Detect FromSqlRaw / ExecuteSqlRaw / ExecuteSqlInterpolated
        var efRawMatches = Regex.Matches(content, @"\.(FromSqlRaw|ExecuteSqlRaw|ExecuteSqlInterpolated|FromSqlInterpolated)\s*\(\s*(?:""([^""]*)""|@""([^""]*)""|(\$""[^""]*""))", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in efRawMatches)
        {
            var sql = match.Groups[2].Success ? match.Groups[2].Value :
                      match.Groups[3].Success ? match.Groups[3].Value :
                      match.Groups[4].Value;
            var tables = ExtractTableNamesFromSql(sql);
            var method = ExtractMethodName(methodNames.Value, match.Index);
            foreach (var table in tables)
            {
                references.Add(new CodeReference
                {
                    FilePath = filePath,
                    LineNumber = GetLineNumber(lineEnds, match.Index),
                    ReferenceType = CodeReferenceType.RawSql,
                    TableName = table.Table,
                    SchemaName = table.Schema,
                    ClassName = className,
                    MethodName = method,
                    CodeSnippet = TruncateSnippet(match.Value, 500)
                });
            }
        }

        // Detect migration CreateTable
        var migrationMatches = Regex.Matches(content, @"migrationBuilder\.(CreateTable|AddColumn|DropTable|DropColumn|RenameTable|RenameColumn|AlterColumn)\s*(?:<[^>]+>)?\s*\(\s*(?:name:\s*)?""([^""]+)""", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in migrationMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lineEnds, match.Index),
                ReferenceType = CodeReferenceType.Migration,
                TableName = match.Groups[2].Value,
                ClassName = className,
                CodeSnippet = TruncateSnippet(match.Value, 500)
            });
        }

        // Detect API endpoints: [HttpGet], [HttpPost], MapGet, MapPost
        var apiMatches = Regex.Matches(content, @"\[(Http(?:Get|Post|Put|Delete|Patch))\s*(?:\(""([^""]*)""\))?\]", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in apiMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lineEnds, match.Index),
                ReferenceType = CodeReferenceType.ApiEndpoint,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect minimal API: app.MapGet/MapPost/MapPut/MapDelete
        var minimalApiMatches = Regex.Matches(content, @"\.(MapGet|MapPost|MapPut|MapDelete|MapPatch)\s*\(\s*""([^""]+)""", RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in minimalApiMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lineEnds, match.Index),
                ReferenceType = CodeReferenceType.ApiEndpoint,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        return references;
    }

    private static string? ExtractClassName(string content)
    {
        var match = Regex.Match(content, @"(?:class|record|struct)\s+(\w+)", RegexOptions.None, MatchTimeout);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static List<(int End, string Name)> FindMethodNames(string content)
    {
        var results = new List<(int, string)>();
        var matches = Regex.Matches(content, @"(?:public|private|protected|internal|static|async|override|virtual)\s+[\w<>\[\]?,\s]+\s+(\w+)\s*\(", RegexOptions.None, MatchTimeout);
        foreach (Match match in matches)
        {
            results.Add((match.Index + match.Length, match.Groups[1].Value));
        }

        return results;
    }

    private static string? ExtractMethodName(List<(int End, string Name)> methodNames, int position)
    {
        // The enclosing method is the last declaration that ends at or before the reference. The list is in source
        // order, so its ends only grow: binary search for the first one past the position.
        var low = 0;
        var high = methodNames.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (methodNames[mid].End > position)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low == 0 ? null : methodNames[low - 1].Name;
    }

    // lineEnds[i] is the offset just past line i's '\n' (the last one past the end of the content): the offsets at which a
    // character index moves on to the next line.
    private static int[] BuildLineEnds(string content)
    {
        var ends = new List<int>();
        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] == '\n')
            {
                ends.Add(i + 1);
            }
        }

        ends.Add(content.Length + 1);
        return [.. ends];
    }

    private static int GetLineNumber(int[] lineEnds, int charIndex)
    {
        // The first line whose end is past the index; an index beyond the content belongs to the last line.
        var low = 0;
        var high = lineEnds.Length;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (lineEnds[mid] > charIndex)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low < lineEnds.Length ? low + 1 : lineEnds.Length;
    }

    private static List<(string? Schema, string Table)> ExtractTableNamesFromSql(string sql)
    {
        var results = new List<(string?, string)>();
        // Match FROM/JOIN/INTO/UPDATE table references
        var matches = Regex.Matches(sql, @"(?:FROM|JOIN|INTO|UPDATE|INSERT\s+INTO)\s+(?:""?(\w+)""?\.)?""?(\w+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled, MatchTimeout);
        foreach (Match match in matches)
        {
            var schema = match.Groups[1].Success ? match.Groups[1].Value : null;
            var table = match.Groups[2].Value;
            // Skip SQL keywords that might be mistaken for table names
            if (!IsSqlKeyword(table))
                results.Add((schema, table));
        }
        return results;
    }

    private static bool IsSqlKeyword(string word)
    {
        var keywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "EXISTS",
            "SET", "VALUES", "NULL", "AS", "ON", "LEFT", "RIGHT", "INNER",
            "OUTER", "CROSS", "GROUP", "ORDER", "BY", "HAVING", "LIMIT",
            "OFFSET", "UNION", "ALL", "DISTINCT", "TOP", "WITH"
        };
        return keywords.Contains(word);
    }

    private static string TruncateSnippet(string text, int maxLength)
        => text.Length <= maxLength ? text : text[..maxLength] + "...";
}
