using System.Reflection;
using System.Text;
using Beacon.Core.HostDocs;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.HostDocs;

[TestFixture]
public class HostDocsDiscoveryTests
{
    private string _root = null!;
    private string _outside = null!;

    [SetUp]
    public void SetUp()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "beacon-hostdocs-" + Guid.NewGuid().ToString("N"));
        _root = Path.Combine(baseDir, "app");
        _outside = Path.Combine(baseDir, "outside");
        Directory.CreateDirectory(Path.Combine(_root, "docs", "wiki", "40-web"));
        Directory.CreateDirectory(Path.Combine(_root, "docs", "wiki", "drafts"));
        Directory.CreateDirectory(_outside);

        Write("docs/wiki/README.md", "# Platform\n\nOverview.");
        Write("docs/wiki/40-web/admin.md", "# Admin\n\nBack office.");
        Write("docs/wiki/40-web/notes.txt", "Plain notes.");
        Write("docs/wiki/40-web/diagram.png", "not a document");
        Write("docs/wiki/drafts/wip.md", "# WIP");
        File.WriteAllText(Path.Combine(_outside, "secret.md"), "# Secret");
    }

    [TearDown]
    public void TearDown()
    {
        Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }

    [Test]
    public void Directory_DefaultGlob_FindsMarkdownRecursively_WithForwardSlashPaths()
    {
        var result = Discover(x => x.FromDirectory("docs/wiki"));

        result.Documents.Select(x => x.Path).Should().Equal("40-web/admin.md", "README.md", "drafts/wip.md");
        result.Documents.Should().OnlyContain(x => x.SourceKey == "docs:dir:docs/wiki");
        result.Documents.Should().OnlyContain(x => x.ContentHash.Length == 64);
    }

    [Test]
    public void Directory_ExplicitGlobs_IngestOnlyAllowedExtensions()
    {
        var result = Discover(x => x.FromDirectory("docs/wiki", "**/*"));

        result.Documents.Select(x => x.Path).Should().Contain("40-web/notes.txt");
        result.Documents.Select(x => x.Path).Should().NotContain("40-web/diagram.png");
    }

    [Test]
    public void Directory_Exclude_DropsMatchingPaths()
    {
        var result = Discover(x => x.FromDirectory("docs/wiki").Exclude("drafts/**"));

        result.Documents.Select(x => x.Path).Should().Equal("40-web/admin.md", "README.md");
    }

    [Test]
    public void Directory_FileOverMaxBytes_IsSkippedWithItsPathOnly()
    {
        Write("docs/wiki/big.md", new string('x', 2048));

        var result = Discover(x =>
        {
            x.FromDirectory("docs/wiki");
            x.MaxDocumentBytes = 1024;
        });

        result.Documents.Select(x => x.Path).Should().NotContain("big.md");
        result.Skipped.Should().ContainSingle()
            .Which.Should().Match<SkippedHostDocument>(x => x.Path == "big.md" && x.Reason.Contains("MaxDocumentBytes"));
    }

    [Test]
    public void Directory_SymlinkedFile_PointingOutside_IsRejected()
    {
        File.CreateSymbolicLink(Path.Combine(_root, "docs", "wiki", "linked.md"), Path.Combine(_outside, "secret.md"));

        var result = Discover(x => x.FromDirectory("docs/wiki"));

        result.Documents.Select(x => x.Path).Should().NotContain("linked.md");
        result.Documents.Should().NotContain(x => x.RawContent.Contains("Secret"));
        result.Skipped.Should().Contain(x => x.Path == "linked.md" && x.Reason.Contains("symbolic link"));
    }

    [Test]
    public void Directory_SymlinkedDirectory_PointingOutside_IsRejected()
    {
        Directory.CreateSymbolicLink(Path.Combine(_root, "docs", "wiki", "external"), _outside);

        var result = Discover(x => x.FromDirectory("docs/wiki"));

        result.Documents.Should().NotContain(x => x.RawContent.Contains("Secret"));
        result.Documents.Select(x => x.Path).Should().NotContain(x => x.StartsWith("external/"));
    }

    [Test]
    public void Directory_Missing_Throws()
    {
        var act = () => Discover(x => x.FromDirectory("docs/nope"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*docs/nope*does not exist*");
    }

    [TestCase("../outside/*.md")]
    [TestCase("**/../../*.md")]
    public void Glob_WithParentSegment_IsRejectedAtRegistration(string glob)
    {
        var act = () => new HostDocsOptions().FromDirectory("docs/wiki", glob);

        act.Should().Throw<ArgumentException>().WithMessage("*must not contain '..'*");
    }

    [Test]
    public void AbsoluteDirectory_IsUsedAsIs()
    {
        var result = Discover(x => x.FromDirectory(Path.Combine(_root, "docs", "wiki", "40-web")));

        result.Documents.Select(x => x.Path).Should().Equal("admin.md");
    }

    [Test]
    public void EmbeddedResources_UsePrefixRemainderAsPath_AndHonourExcludesAndSize()
    {
        var assembly = new FakeResourceAssembly(new Dictionary<string, string>
        {
            ["docs/wiki/README.md"] = "# Platform",
            ["docs/wiki\\40-web\\admin.md"] = "# Admin",
            ["docs/wiki/drafts/wip.md"] = "# WIP",
            ["docs/wiki/logo.png"] = "binary",
            ["docs/wiki/big.md"] = new string('x', 2048),
            ["Other.Resource.md"] = "# Not under the prefix"
        });

        var result = Discover(x =>
        {
            x.FromEmbeddedResources(assembly, "docs/wiki/").Exclude("drafts/**");
            x.MaxDocumentBytes = 1024;
        });

        result.Documents.Select(x => x.Path).Should().Equal("40-web/admin.md", "README.md");
        result.Documents.Should().OnlyContain(x => x.SourceKey == "docs:res:Fake.Host:docs/wiki/");
        result.Skipped.Select(x => x.Path).Should().Equal("big.md");
    }

    [Test]
    public void ContentHash_IsStableAndChangesWithContent()
    {
        var first = Discover(x => x.FromDirectory("docs/wiki")).Documents.Single(x => x.Path == "README.md").ContentHash;
        var again = Discover(x => x.FromDirectory("docs/wiki")).Documents.Single(x => x.Path == "README.md").ContentHash;
        Write("docs/wiki/README.md", "# Platform\n\nChanged.");
        var changed = Discover(x => x.FromDirectory("docs/wiki")).Documents.Single(x => x.Path == "README.md").ContentHash;

        again.Should().Be(first);
        changed.Should().NotBe(first);
    }

    private HostDocsDiscoveryResult Discover(Action<HostDocsOptions> configure)
    {
        var options = new HostDocsOptions { ProjectName = "Netgiro" };
        configure(options);

        return HostDocsDiscovery.Discover(new HostDocsRegistration(options), _root);
    }

    private void Write(string relativePath, string content)
    {
        File.WriteAllText(Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar)), content);
    }

    internal sealed class FakeResourceAssembly(Dictionary<string, string> resources) : Assembly
    {
        public override string[] GetManifestResourceNames() => resources.Keys.ToArray();

        public override Stream? GetManifestResourceStream(string name) =>
            resources.TryGetValue(name, out var content) ? new MemoryStream(Encoding.UTF8.GetBytes(content)) : null;

        public override AssemblyName GetName() => new("Fake.Host");
    }
}
