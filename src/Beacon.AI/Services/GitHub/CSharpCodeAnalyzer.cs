using System.Text.RegularExpressions;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.GitHub;

internal sealed class CSharpCodeAnalyzer : ICodeAnalyzer
{
    public string Language => "C#";

    private static readonly string[] CSharpExtensions = { ".cs" };

    public bool CanAnalyze(string filePath) =>
        CSharpExtensions.Any(ext => filePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    public List<CodeReference> AnalyzeFile(string filePath, string content)
    {
        var references = new List<CodeReference>();
        var lines = content.Split('\n');
        var className = ExtractClassName(content);

        // Detect EF Core DbSet<T> declarations
        var dbSetMatches = Regex.Matches(content, @"DbSet<(\w+)>\s+(\w+)", RegexOptions.Compiled);
        foreach (Match match in dbSetMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lines, match.Index),
                ReferenceType = CodeReferenceType.EntityModel,
                TableName = match.Groups[1].Value,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect ToTable("tablename") or ToTable("tablename", "schema")
        var toTableMatches = Regex.Matches(content, @"\.ToTable\(\s*""([^""]+)""(?:\s*,\s*""([^""]+)"")?\s*\)", RegexOptions.Compiled);
        foreach (Match match in toTableMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lines, match.Index),
                ReferenceType = CodeReferenceType.DbContextConfiguration,
                TableName = match.Groups[1].Value,
                SchemaName = match.Groups[2].Success ? match.Groups[2].Value : null,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect [Table("name")] attribute
        var tableAttrMatches = Regex.Matches(content, @"\[Table\(\s*""([^""]+)""(?:\s*,\s*Schema\s*=\s*""([^""]+)"")?\s*\)\]", RegexOptions.Compiled);
        foreach (Match match in tableAttrMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lines, match.Index),
                ReferenceType = CodeReferenceType.EntityModel,
                TableName = match.Groups[1].Value,
                SchemaName = match.Groups[2].Success ? match.Groups[2].Value : null,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect Dapper queries: connection.Query/Execute/QueryAsync/ExecuteAsync
        var dapperMatches = Regex.Matches(content, @"\.(Query|Execute|QueryAsync|ExecuteAsync|QueryFirst|QuerySingle|QueryFirstOrDefault|QuerySingleOrDefault)\w*(?:<[^>]+>)?\s*\(\s*(?:""([^""]*)""|@""([^""]*)""|(\$""[^""]*""))", RegexOptions.Compiled);
        foreach (Match match in dapperMatches)
        {
            var sql = match.Groups[2].Success ? match.Groups[2].Value :
                      match.Groups[3].Success ? match.Groups[3].Value :
                      match.Groups[4].Value;
            var tables = ExtractTableNamesFromSql(sql);
            var method = ExtractMethodName(content, match.Index);
            foreach (var table in tables)
            {
                references.Add(new CodeReference
                {
                    FilePath = filePath,
                    LineNumber = GetLineNumber(lines, match.Index),
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
        var efRawMatches = Regex.Matches(content, @"\.(FromSqlRaw|ExecuteSqlRaw|ExecuteSqlInterpolated|FromSqlInterpolated)\s*\(\s*(?:""([^""]*)""|@""([^""]*)""|(\$""[^""]*""))", RegexOptions.Compiled);
        foreach (Match match in efRawMatches)
        {
            var sql = match.Groups[2].Success ? match.Groups[2].Value :
                      match.Groups[3].Success ? match.Groups[3].Value :
                      match.Groups[4].Value;
            var tables = ExtractTableNamesFromSql(sql);
            var method = ExtractMethodName(content, match.Index);
            foreach (var table in tables)
            {
                references.Add(new CodeReference
                {
                    FilePath = filePath,
                    LineNumber = GetLineNumber(lines, match.Index),
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
        var migrationMatches = Regex.Matches(content, @"migrationBuilder\.(CreateTable|AddColumn|DropTable|DropColumn|RenameTable|RenameColumn|AlterColumn)\s*(?:<[^>]+>)?\s*\(\s*(?:name:\s*)?""([^""]+)""", RegexOptions.Compiled);
        foreach (Match match in migrationMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lines, match.Index),
                ReferenceType = CodeReferenceType.Migration,
                TableName = match.Groups[2].Value,
                ClassName = className,
                CodeSnippet = TruncateSnippet(match.Value, 500)
            });
        }

        // Detect API endpoints: [HttpGet], [HttpPost], MapGet, MapPost
        var apiMatches = Regex.Matches(content, @"\[(Http(?:Get|Post|Put|Delete|Patch))\s*(?:\(""([^""]*)""\))?\]", RegexOptions.Compiled);
        foreach (Match match in apiMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lines, match.Index),
                ReferenceType = CodeReferenceType.ApiEndpoint,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        // Detect minimal API: app.MapGet/MapPost/MapPut/MapDelete
        var minimalApiMatches = Regex.Matches(content, @"\.(MapGet|MapPost|MapPut|MapDelete|MapPatch)\s*\(\s*""([^""]+)""", RegexOptions.Compiled);
        foreach (Match match in minimalApiMatches)
        {
            references.Add(new CodeReference
            {
                FilePath = filePath,
                LineNumber = GetLineNumber(lines, match.Index),
                ReferenceType = CodeReferenceType.ApiEndpoint,
                ClassName = className,
                CodeSnippet = match.Value.Trim()
            });
        }

        return references;
    }

    private static string? ExtractClassName(string content)
    {
        var match = Regex.Match(content, @"(?:class|record|struct)\s+(\w+)");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractMethodName(string content, int position)
    {
        // Look backwards from position to find enclosing method
        var before = content[..Math.Min(position, content.Length)];
        var matches = Regex.Matches(before, @"(?:public|private|protected|internal|static|async|override|virtual)\s+[\w<>\[\]?,\s]+\s+(\w+)\s*\(");
        return matches.Count > 0 ? matches[^1].Groups[1].Value : null;
    }

    private static int GetLineNumber(string[] lines, int charIndex)
    {
        var count = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            count += lines[i].Length + 1; // +1 for \n
            if (count > charIndex) return i + 1;
        }
        return lines.Length;
    }

    private static List<(string? Schema, string Table)> ExtractTableNamesFromSql(string sql)
    {
        var results = new List<(string?, string)>();
        // Match FROM/JOIN/INTO/UPDATE table references
        var matches = Regex.Matches(sql, @"(?:FROM|JOIN|INTO|UPDATE|INSERT\s+INTO)\s+(?:""?(\w+)""?\.)?""?(\w+)""?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
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
