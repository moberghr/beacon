using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every regular expression in <c>src/</c> carries a match timeout, so text from users, catalogs or an LLM cannot hold a
/// thread through backtracking. A constructor, a static <c>Regex.*</c> call or a <c>[GeneratedRegex]</c> without a timeout
/// (or <c>RegexOptions.NonBacktracking</c>) fails here, unless the file is in <see cref="Allowed"/> with its reason.
/// </summary>
[TestFixture]
public class RegexTimeoutConventionTests
{
    // Path (relative to the repository root, forward slashes) -> why its untimed patterns are safe.
    private static readonly Dictionary<string, string> Allowed = new();

    [Test]
    public void EveryRegexInSourceHasAMatchTimeout()
    {
        var root = RepositoryRoot();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/Beacon.Tests/") || relative.StartsWith("src/Beacon.Tests/") ||
                relative.Contains("/Migrations/") || relative.Contains("/obj/") || relative.Contains("/bin/"))
            {
                continue;
            }

            if (Allowed.ContainsKey(relative))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var line in UntimedRegexLines(text))
            {
                offenders.Add($"{relative}:{line}");
            }
        }

        string.Join("\n", offenders).Should().BeEmpty("every Regex needs a match timeout (a TimeSpan argument, matchTimeoutMilliseconds, or NonBacktracking)");
    }

    [Test]
    public void Scanner_FlagsUntimedAndAcceptsTimedForms()
    {
        var untimed = """
            var a = new Regex(@"a+");
            var b = Regex.IsMatch(x, "b");
            [GeneratedRegex("c")]
            private static readonly Regex D = new(@"d", RegexOptions.Compiled);
            """;
        var timed = """
            var a = new Regex(@"a+", RegexOptions.None, TimeSpan.FromSeconds(1));
            var b = Regex.IsMatch(x, "b", RegexOptions.None, MatchTimeout);
            [GeneratedRegex("c", RegexOptions.None, matchTimeoutMilliseconds: 250)]
            private static readonly Regex D = new(@"d", RegexOptions.NonBacktracking);
            """;

        UntimedRegexLines(untimed).Should().Equal(1, 2, 3, 4);
        UntimedRegexLines(timed).Should().BeEmpty();
    }

    private static List<int> UntimedRegexLines(string text)
    {
        var lines = new List<int>();
        var starts = Regex.Matches(
            text,
            @"new Regex\(|(?<!\w)Regex\.(?:IsMatch|Match|Matches|Replace|Split|EnumerateMatches|Count)\(|\[GeneratedRegex\(|\bRegex\s+\w+\s*=\s*new\(",
            RegexOptions.None,
            TimeSpan.FromSeconds(5));

        foreach (Match start in starts)
        {
            var open = text.IndexOf('(', start.Index + start.Length - 1);
            var call = text[start.Index..(CloseOf(text, open) + 1)];
            if (!Regex.IsMatch(call, @"Timeout|TimeSpan|NonBacktracking", RegexOptions.None, TimeSpan.FromSeconds(5)))
            {
                lines.Add(text.Take(start.Index).Count(x => x == '\n') + 1);
            }
        }

        return lines;
    }

    // Index of the parenthesis that closes the one at open, skipping string literals.
    private static int CloseOf(string text, int open)
    {
        var depth = 0;
        var i = open;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '"')
            {
                var verbatim = i > 0 && (text[i - 1] == '@' || (i > 1 && text[i - 1] == '$' && text[i - 2] == '@'));
                verbatim |= i > 0 && text[i - 1] == '$' && i > 1 && text[i - 2] == '@';
                i++;
                while (i < text.Length)
                {
                    if (verbatim && text[i] == '"' && i + 1 < text.Length && text[i + 1] == '"')
                    {
                        i += 2;
                        continue;
                    }

                    if (!verbatim && text[i] == '\\')
                    {
                        i += 2;
                        continue;
                    }

                    if (text[i] == '"')
                    {
                        break;
                    }

                    i++;
                }
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }
            }

            i++;
        }

        return text.Length - 1;
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
