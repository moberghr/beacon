using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.11: no log call in shipped code hands the logger an exception object — as its exception argument or as a template
/// argument — and no <c>[LoggerMessage]</c> method takes one. An exception's message and stack text can quote row values,
/// SQL, hosts or credentials, and a log sink records them whole; a failure is named by <c>FailureCodes.Describe</c> (type,
/// error code, inner types, throwing method) instead. The test project, the test helpers and the sample hosts (never
/// shipped) are not scanned.
/// </summary>
[TestFixture]
public class ExceptionLogConventionTests
{
    private static readonly string[] NotShipped = ["/Beacon.Tests/", "/Beacon.Testing/", "/Beacon.SampleProject/", "/Beacon.SampleGateway/"];

    private static readonly Regex LogCall = new(
        @"\.Log(?:Trace|Debug|Information|Warning|Error|Critical)?\s*\(",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex LoggerMessageMethod = new(
        @"\[LoggerMessage\b[^\]]*\]\s*(?:[\w<>?]+\s+)*?partial\s+void\s+\w+\s*\((?<parameters>[^)]*)\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex ExceptionParameter = new(@"\bException\??\s+\w+", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    // An argument that is an exception object by its name.
    private static readonly Regex ExceptionName = new(
        @"^(?:ex|e|exception|inner|innerException|\w+Ex|\w+Exception|[\w.?]+\.(?:Exception|InnerException|Failure))!?$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private static readonly Regex StringLiteral = new(@"@?\$?""(?:[^""\\]|\\.)*""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    [Test]
    public void ShippedCode_NeverLogsAnExceptionObject()
    {
        var root = RepositoryRoot();
        var violations = new List<string>();
        var calls = 0;
        foreach (var file in ShippedSources(root))
        {
            var text = File.ReadAllText(file);
            foreach (Match call in LogCall.Matches(text))
            {
                calls++;
                if (PassesAnException(Arguments(text, call.Index + call.Length - 1)))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, call.Index)}");
                }
            }

            foreach (Match method in LoggerMessageMethod.Matches(text))
            {
                if (ExceptionParameter.IsMatch(method.Groups["parameters"].Value))
                {
                    violations.Add($"{Path.GetRelativePath(root, file)}:{LineOf(text, method.Index)}");
                }
            }
        }

        calls.Should().BeGreaterThan(500, "the scan reads every shipped source");
        violations.Should().BeEmpty(
            "a log names a failure by FailureCodes.Describe, never by the exception itself (found: {0})",
            string.Join(", ", violations));
    }

    [Test]
    public void TheScan_FindsAnExceptionHandedToTheLogger_AndLetsItsDescriptionThrough()
    {
        PassesAnException("(ex, \"failed\")").Should().BeTrue();
        PassesAnException("(\n    ex,\n    \"failed {Id}\",\n    id)").Should().BeTrue();
        PassesAnException("(LogLevel.Error, ex, \"failed\")").Should().BeTrue();
        PassesAnException("(EventIds.Failed, exception, \"failed\")").Should().BeTrue();
        PassesAnException("(context.Failure, \"failed\")").Should().BeTrue();
        PassesAnException("(\"failed {Error}\", ex)").Should().BeTrue();
        PassesAnException("(\"failed {Error}\", httpEx)").Should().BeTrue();
        PassesAnException("(\"failed, see ex, {Id}\", id)").Should().BeFalse();
        PassesAnException("(\"failed {Failure}\", FailureCodes.Describe(ex))").Should().BeFalse();
        PassesAnException("(\"failed {Type}\", ex.GetType().Name)").Should().BeFalse();

        LoggerMessageMethod.Match("[LoggerMessage(Level = LogLevel.Error, Message = \"x\")]\n    private static partial void LogFailed(ILogger logger, Exception exception, int failures);")
            .Groups["parameters"].Value.Should().MatchRegex(ExceptionParameter.ToString());
    }

    private static bool PassesAnException(string arguments)
    {
        var screened = StringLiteral.Replace(arguments, "\"\"");

        return TopLevelArguments(screened)
            .Any(x => ExceptionName.IsMatch(x));
    }

    // The top-level arguments of a parenthesised argument list whose string literals are already screened.
    private static IEnumerable<string> TopLevelArguments(string arguments)
    {
        var inner = arguments.Trim();
        inner = inner.StartsWith('(') ? inner[1..] : inner;
        inner = inner.EndsWith(')') ? inner[..^1] : inner;

        var depth = 0;
        var start = 0;
        for (var i = 0; i < inner.Length; i++)
        {
            var c = inner[i];
            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                yield return inner[start..i].Trim();
                start = i + 1;
            }
        }

        yield return inner[start..].Trim();
    }

    // The call's parenthesised argument list starting at the '(' at open, string literals skipped.
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

    private static IEnumerable<string> ShippedSources(string root) =>
        Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(x => !IsExcluded(x.Replace('\\', '/')));

    private static bool IsExcluded(string path) =>
        NotShipped.Any(x => path.Contains(x, StringComparison.Ordinal))
        || path.Contains("/obj/", StringComparison.Ordinal)
        || path.Contains("/bin/", StringComparison.Ordinal)
        || path.Contains("/Migrations/", StringComparison.Ordinal)
        || path.Contains("/node_modules/", StringComparison.Ordinal);

    private static int LineOf(string text, int index) => text[..index].Count(x => x == '\n') + 1;

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
