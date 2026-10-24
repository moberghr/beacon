using System.Text.RegularExpressions;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// The connection resolver is the one place a governed data source is held: it reads the entity's
/// <c>ApprovedTargetHash</c>. A <c>DataSource</c> production code builds itself (a projection, a temporary or a new row)
/// carries whatever it was given, so one built without the hash would connect as a source not governed. Every such
/// construction in production code is listed here with the reason it is safe: it sets the hash, or it never reaches
/// <c>GetConnectionString</c> / <c>GetConnectionData</c> / <c>GetWriteConnectionString</c>. A new one fails this test until it
/// is reviewed and listed.
/// </summary>
[TestFixture]
public class HeldSourceProjectionTests
{
    // Per file and enclosing member, one reason per construction; SetsTheHash marks one whose initializer must name it.
    private static readonly Construction[] Constructions =
    [
        new("src/Beacon.Core/Services/DataSourceService.cs", "CreateDataSource", "the new row, which records ApprovedTargetHash itself when the workflow governs connections", SetsTheHash: true),
        new("src/Beacon.Core/Services/DataSourceService.cs", "TestConnectionAsync", "a probe with the credentials the caller typed, deliberately outside the approval"),
        new("src/Beacon.Core/HostData/HostDataSourceSynchronizer.cs", "SyncSourceAsync", "a host-managed source, which the approval never governs"),
        new("src/Beacon.Core/Services/Providers/DataSourceTransportStatus.cs", "VerifiedAsync", "feeds InspectTransport only, which is not gated and makes no connection"),
        new("src/Beacon.AI/Services/Ai/AiActor/AiActorService.cs", "GetActorAsync", "a display projection with empty connection data, never handed to the resolver")
    ];

    private static readonly Regex[] ConstructionPatterns =
    [
        // new DataSource { … }, new DataSource() { … }, new Data.Entities.DataSource { … }
        new(@"\bnew\s+(?:[\w.]+\.)?DataSource\s*(?:\(\s*\))?\s*\{", RegexOptions.CultureInvariant),

        // DataSource source = new() / DataSource? source = new(…)
        new(@"\bDataSource\??\s+\w+\s*=\s*new\s*\(", RegexOptions.CultureInvariant),

        // DataSource = new() { … } (a navigation property, target-typed)
        new(@"\bDataSource\s*=\s*new\s*\(\s*\)", RegexOptions.CultureInvariant)
    ];

    // A member declaration: its modifiers, its type and its name before the parameter list.
    private static readonly Regex MemberPattern = new(
        @"(?:public|private|internal|protected)(?:\s+(?:static|async|override|virtual|sealed|new|readonly|partial))*\s+[\w<>\[\]?,. ()]+?\s+(\w+)\s*(?:<[^>()]*>)?\s*\(",
        RegexOptions.CultureInvariant);

    [Test]
    public void EveryProductionDataSourceConstruction_IsListedWithAReason()
    {
        var found = ReadProductionSources()
            .SelectMany(x => Found(x.Key, x.Value))
            .ToList();

        found.Should().Contain(x => x.File == "src/Beacon.Core/Services/DataSourceService.cs", "the scan reads the production sources");
        found.Select(x => $"{x.File}#{x.Member}").Should().BeEquivalentTo(
            Constructions.Select(x => $"{x.File}#{x.Member}"),
            "a DataSource built in production code must set ApprovedTargetHash or never reach the connection resolver; review a new one and list it, by its file and member, with its reason");
        foreach (var listed in Constructions.Where(x => x.SetsTheHash))
        {
            found.Should().Contain(x => x.File == listed.File && x.Member == listed.Member && x.Initializer.Contains("ApprovedTargetHash", StringComparison.Ordinal), listed.Reason);
        }
    }

    // The scan itself: a construction in a member nobody listed is found, by that member.
    [Test]
    public void AConstructionInAnUnlistedMember_IsFound()
    {
        const string source = """
            internal sealed class Elsewhere
            {
                public DataSource Build(int id)
                {
                    return new DataSource { Id = id };
                }
            }
            """;

        Found("src/Elsewhere.cs", source).Should().ContainSingle().Which.Member.Should().Be("Build");
    }

    // The constructions in one file, each with its enclosing member and the text of its initializer.
    private static IEnumerable<(string File, string Member, string Initializer)> Found(string file, string text)
    {
        foreach (var match in ConstructionPatterns.SelectMany(x => x.Matches(text)))
        {
            var member = MemberPattern.Matches(text[..match.Index])
                .Select(x => x.Groups[1].Value)
                .LastOrDefault() ?? "(none)";
            var end = text.IndexOf('}', match.Index);

            yield return (file, member, end < 0 ? text[match.Index..] : text[match.Index..(end + 1)]);
        }
    }

    // Production C# under src, without tests, migrations and build output, each file without its whole-line comments so a
    // doc comment that names the type is not a construction.
    private static Dictionary<string, string> ReadProductionSources()
    {
        var root = RepositoryRoot();

        return Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Select(x => Path.GetRelativePath(root, x).Replace('\\', '/'))
            .Where(x => !x.StartsWith("src/Beacon.Tests/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/Migrations/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/obj/", StringComparison.Ordinal))
            .Where(x => !x.Contains("/bin/", StringComparison.Ordinal))
            .ToDictionary(x => x, x => WithoutLineComments(File.ReadAllText(Path.Combine(root, x))), StringComparer.Ordinal);
    }

    private static string WithoutLineComments(string text)
    {
        return string.Join('\n', text
            .Split('\n')
            .Where(x => !x.TrimStart().StartsWith("//", StringComparison.Ordinal)));
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

    private sealed record Construction(string File, string Member, string Reason, bool SetsTheHash = false);
}
