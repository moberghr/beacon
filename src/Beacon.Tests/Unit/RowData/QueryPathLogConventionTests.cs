using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// The convention behind every failure log on a query path (§1.11): a log call names a failure by
/// <c>FailureCodes.Describe</c> (type, code, inner types, throwing method) or a database error by
/// <c>DatabaseErrorRedactor.ClassOnly</c> — it never hands the logger the exception object, an exception's message or a
/// result's error message, which can quote the SQL, row values or a credential. The few log calls that may are listed
/// below with the reason: connection tests (a connection failure names no row) and metadata extraction (catalog names,
/// not rows).
/// </summary>
[TestFixture]
public class QueryPathLogConventionTests
{
    // The query paths: providers and connectors, query services, previews, the in-memory join, migrations, data quality,
    // jobs, saved queries, the MCP tools and services, the ask pipeline, eval, value grounding, actors and learning.
    private static readonly string[] QueryPathFiles =
    [
        "src/Beacon.Core/Services/Providers/DatabaseProvider.cs",
        "src/Beacon.Core/Services/GatedSqlRunner.cs",
        "src/Beacon.Core/Services/QueryExecutionPreviewService.cs",
        "src/Beacon.Core/Handlers/Queries/Execute*PreviewHandler.cs",
        "src/Beacon.Core/Services/InMemoryDatabaseManager.cs",
        "src/Beacon.Core/Services/VirtualTableManager.cs",
        "src/Beacon.Core/Services/DataSourceService.cs",
        "src/Beacon.Core/Services/DataQualityEvaluationService.cs",
        "src/Beacon.Core/Services/MigrationService*.cs",
        "src/Beacon.Core/Services/QueryService*.cs",
        "src/Beacon.Core/Services/Metadata/ColumnValueSampler.cs",
        "src/Beacon.Core/SavedQueries/*.cs",
        "src/Beacon.Core/Worker/Services/JobService.cs",
        "src/Beacon.Connector.Api/ApiProvider.cs",
        "src/Beacon.Connector.BigQuery/BigQueryProvider.cs",
        "src/Beacon.Connector.CloudWatch/CloudWatchProvider.cs",
        "src/Beacon.Connector.Databricks/DatabricksProvider.cs",
        "src/Beacon.MCP/Tools/*.cs",
        "src/Beacon.MCP/Services/*.cs",
        "src/Beacon.MCP/SavedQueries/*.cs",
        "src/Beacon.AI/Services/Mcp/*.cs",
        "src/Beacon.AI/Services/Eval/*.cs",
        "src/Beacon.AI/Services/Knowledge/ValueGroundingService.cs",
        "src/Beacon.AI/Services/Knowledge/KnowledgeGraphService.cs",
        "src/Beacon.AI/Services/Ai/AiActor/AiActorService.cs",
        "src/Beacon.AI/Services/Learning/*.cs",
        "src/Beacon.AI/Services/Documentation/ProjectDocumentationService.cs"
    ];

    // File and a fragment of the log call that may name the exception or a message, with why.
    private static readonly (string File, string Fragment, string Reason)[] Allowed =
    [
        ("DatabaseProvider.cs", "Connection test failed for database data source", "a connection test reads no row"),
        ("ApiProvider.cs", "Connection test failed", "a connection test reads no row"),
        ("BigQueryProvider.cs", "Connection test failed", "a connection test reads no row"),
        ("CloudWatchProvider.cs", "Connection test failed", "a connection test reads no row"),
        ("DatabricksProvider.cs", "Connection test failed", "a connection test reads no row"),
        ("DataSourceService.cs", "Failed to import API metadata", "metadata import reads the API's description, not rows"),
        ("ApiProvider.cs", "Metadata retrieval failed", "metadata retrieval reads the API's description, not rows"),
        ("CloudWatchProvider.cs", "Metadata retrieval failed", "metadata retrieval reads log group names, not log events"),
        ("AskSqlPipeline.cs", "Schema pre-validation failed", "the schema check's own message names the statement's columns, never a value"),
        ("CrossSourceQueryService.cs", "Schema pre-validation failed", "the schema check's own message names the statement's columns, never a value")
    ];

    private static readonly Regex LogCall = new(@"\b_?[Ll]ogger\??\.Log(?:Trace|Debug|Information|Warning|Error|Critical)?\s*\(", RegexOptions.CultureInvariant);

    // Safe wrappers whose argument is an exception or an error text by design.
    private static readonly Regex SafeWrapper = new(@"FailureCodes\.(?:Describe|ErrorCodeOf|InnerTypesOf|ThrowingMethodOf)\((?:[^()]|\([^()]*\))*\)|DatabaseErrorRedactor\.ClassOnly\((?:[^()]|\([^()]*\))*\)|\w+\.GetType\(\)\.Name", RegexOptions.CultureInvariant);

    // An exception handed to the logger as its exception argument (first, or after an event id / level).
    private static readonly Regex ExceptionArgument = new(@"^\(\s*(?:[\w.]+\s*,\s*){0,2}(?:ex|e|\w+Ex|exception|\w+Exception)\s*,", RegexOptions.CultureInvariant);

    private static readonly Regex MessageText = new(@"\.(?:Message|ErrorMessage|ToString\(\))\b|\bErrorMessage\b", RegexOptions.CultureInvariant);

    private static readonly Regex StringLiteral = new(@"@?\$?""(?:[^""\\]|\\.)*""", RegexOptions.CultureInvariant);

    [Test]
    public void QueryPathLogs_NeverCarryAnExceptionOrItsMessage()
    {
        var root = RepositoryRoot();
        var files = QueryPathFiles
            .SelectMany(x => Directory.GetFiles(Path.Combine(root, Path.GetDirectoryName(x)!), Path.GetFileName(x)))
            .Distinct()
            .ToList();
        files.Should().HaveCountGreaterThan(QueryPathFiles.Length - 1, "every listed path still exists");

        var violations = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match call in LogCall.Matches(text))
            {
                var arguments = Arguments(text, call.Index + call.Length - 1);
                if (Allowed.Any(x => Path.GetFileName(file) == x.File && arguments.Contains(x.Fragment, StringComparison.Ordinal)))
                {
                    continue;
                }

                if (Violates(arguments))
                {
                    var line = text[..call.Index].Count(x => x == '\n') + 1;
                    violations.Add($"{Path.GetRelativePath(root, file)}:{line}");
                }
            }
        }

        violations.Should().BeEmpty(
            "a query-path log names a failure by FailureCodes.Describe or DatabaseErrorRedactor.ClassOnly (found: {0})",
            string.Join(", ", violations));
    }

    [Test]
    public void TheConvention_CatchesAnExceptionArgumentAndAMessage()
    {
        Violates("(ex, \"failed\")").Should().BeTrue();
        Violates("(LogLevel.Error, ex, \"failed\")").Should().BeTrue();
        Violates("(\"failed {Error}\", ex.Message)").Should().BeTrue();
        Violates("(\"failed {Error}\", result.ErrorMessage)").Should().BeTrue();
        Violates("(\"failed {Failure}\", FailureCodes.Describe(ex))").Should().BeFalse();
        Violates("(\"failed {Class}\", DatabaseErrorRedactor.ClassOnly(result.ErrorMessage ?? \"\", logger))").Should().BeFalse();
        Violates("(\"failed {Type}\", ex.GetType().Name)").Should().BeFalse();
    }

    private static bool Violates(string arguments)
    {
        var screened = SafeWrapper.Replace(StringLiteral.Replace(arguments, "\"\""), "_");

        return ExceptionArgument.IsMatch(screened) || MessageText.IsMatch(screened);
    }

    // The argument list of the call whose opening parenthesis is at open, balanced and with string literals skipped.
    private static string Arguments(string text, int open)
    {
        var depth = 0;
        var inString = false;
        for (var i = open; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')' && --depth == 0)
            {
                return text[open..(i + 1)];
            }
        }

        return text[open..];
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return directory!.FullName;
    }
}
