using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.GitHub;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

[TestFixture]
public class GitHubScannerBoundsTests
{
    private const int ProjectId = 1;
    private const int RepositoryId = 10;

    [Test]
    public async Task Scan_OverTheFileCap_ReadsOnlyTheCap_AndSaysSo()
    {
        var handler = new FakeGitHub(Enumerable.Range(0, 7).Select(x => ($"src/F{x}.cs", 100)).ToList());
        var (scanner, repo) = Build(handler, new GitHubScanOptions { MaxFilesPerRepository = 3 });

        await scanner.ScanRepositoryAsync(ProjectId, RepositoryId);

        handler.ContentRequests.Should().Be(3);
        repo.ScanStatus.Should().Be(ScanStatus.Completed);
        repo.TotalFilesScanned.Should().Be(3);
        repo.LastScanError.Should().Contain("Partial scan").And.Contain("first 3 of 7");
    }

    [Test]
    public async Task Scan_SkipsFilesOverTheSizeCap_AndSaysSo()
    {
        var handler = new FakeGitHub([("src/Small.cs", 100), ("src/Big.cs", 5_000)]);
        var (scanner, repo) = Build(handler, new GitHubScanOptions { MaxFileBytes = 1_000 });

        await scanner.ScanRepositoryAsync(ProjectId, RepositoryId);

        handler.ContentRequests.Should().Be(1);
        repo.TotalFilesScanned.Should().Be(1);
        repo.LastScanError.Should().Contain("1 files over 1000 bytes");
    }

    [Test]
    public async Task Scan_ThatReachesTheTimeLimit_EndsPartial_WithTheReason()
    {
        var handler = new FakeGitHub(Enumerable.Range(0, 60).Select(x => ($"src/F{x}.cs", 100)).ToList()) { ContentDelay = TimeSpan.FromMilliseconds(200) };
        var (scanner, repo) = Build(handler, new GitHubScanOptions { ScanTimeout = TimeSpan.FromMilliseconds(300) });

        await scanner.ScanRepositoryAsync(ProjectId, RepositoryId);

        repo.ScanStatus.Should().Be(ScanStatus.Completed);
        repo.TotalFilesScanned.Should().BeLessThan(60);
        repo.LastScanError.Should().Contain("Partial scan").And.Contain("time limit");
    }

    [Test]
    public async Task Scan_CancelledByTheCaller_Fails_AndThrows()
    {
        var handler = new FakeGitHub([("src/A.cs", 100)]) { ContentDelay = TimeSpan.FromSeconds(30) };
        var (scanner, repo) = Build(handler, new GitHubScanOptions());
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var scan = () => scanner.ScanRepositoryAsync(ProjectId, RepositoryId, cts.Token);

        await scan.Should().ThrowAsync<OperationCanceledException>();
        repo.ScanStatus.Should().Be(ScanStatus.Failed);
    }

    [Test]
    public async Task Scan_OfAnotherGitHost_IsRefused_BeforeAnyRequestOrTokenUse()
    {
        var handler = new FakeGitHub([]);
        var (scanner, repo) = Build(handler, new GitHubScanOptions(), "https://dev.azure.com/org/project/_git/core");

        var scan = () => scanner.ScanRepositoryAsync(ProjectId, RepositoryId);

        await scan.Should().ThrowAsync<InvalidOperationException>().WithMessage("*github.com*");
        handler.Requests.Should().Be(0);
        repo.ScanStatus.Should().Be(ScanStatus.Failed);
        repo.LastScanError.Should().Contain("other Git hosts are not supported");
    }

    [TestCase("https://github.com/acme/ledger", "acme", "ledger")]
    [TestCase("https://github.com/acme/ledger.git", "acme", "ledger")]
    [TestCase("https://github.com/acme/my.github.thing/", "acme", "my.github.thing")]
    [TestCase("acme/ledger", "acme", "ledger")]
    public void ParseRepositoryUrl_AcceptsGitHubForms(string url, string owner, string repo)
    {
        GitHubScannerService.ParseRepositoryUrl(url).Should().Be((owner, repo));
    }

    [TestCase("https://github.bank.internal/payments/ledger")]
    [TestCase("https://github.com.evil.example/acme/ledger")]
    [TestCase("http://github.com/acme/ledger")]
    [TestCase("https://gitlab.com/acme/ledger")]
    [TestCase("git@github.com:acme/ledger.git")]
    [TestCase("https://github.com/acme")]
    public void ParseRepositoryUrl_RefusesEverythingElse(string url)
    {
        var parse = () => GitHubScannerService.ParseRepositoryUrl(url);

        parse.Should().Throw<InvalidOperationException>();
    }

    [Test]
    public void AnalyzeFile_WithManyModifiersBeforeAQuery_FinishesWithinTheMatchTimeout()
    {
        var content = string.Concat(Enumerable.Repeat("public a\t", 20_000)) + "\nconn.Query(\"select * from orders\");";

        var analyze = () => new CSharpCodeAnalyzer().AnalyzeFile("A.cs", content);

        var started = DateTime.UtcNow;
        try
        {
            analyze();
        }
        catch (RegexMatchTimeoutException)
        {
        }

        (DateTime.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    [Test]
    public void AnalyzeFile_FindsTheEnclosingMethod_ForEveryReference()
    {
        var content = "class A {\n public void First() {\n  conn.Query(\"select * from orders\");\n }\n public void Second() {\n  conn.Query(\"select * from items\");\n }\n}";

        var refs = new CSharpCodeAnalyzer().AnalyzeFile("A.cs", content);

        refs.Select(x => (x.TableName, x.MethodName)).Should().BeEquivalentTo(new[] { ("orders", "First"), ("items", "Second") });
    }

    private static (GitHubScannerService Scanner, GitHubRepository Repo) Build(FakeGitHub handler, GitHubScanOptions limits, string url = "https://github.com/acme/ledger")
    {
        var repo = new GitHubRepository { Id = RepositoryId, ProjectId = ProjectId, RepositoryUrl = url, Branch = "main" };
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet([repo], saved),
            [typeof(CodeReference)] = RecordingBeaconContext.MemorySet(new List<CodeReference>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var clients = new Mock<IHttpClientFactory>();
        clients.Setup(x => x.CreateClient(It.IsAny<string>())).Returns(() => new HttpClient(handler, false));
        var options = Options.Create(limits);

        var scanner = new GitHubScannerService(
            factory.Object,
            new GitHubApiClient(clients.Object, options),
            [new CSharpCodeAnalyzer()],
            Mock.Of<IEncryptionService>(),
            options,
            NullLogger<GitHubScannerService>.Instance);

        return (scanner, repo);
    }

    private sealed class FakeGitHub(List<(string Path, int Size)> files) : HttpMessageHandler
    {
        private int _requests;
        private int _contentRequests;

        public TimeSpan ContentDelay { get; init; }

        public int Requests => _requests;

        public int ContentRequests => _contentRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/contents/", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _contentRequests);
                await Task.Delay(ContentDelay, cancellationToken);
                var body = Convert.ToBase64String(Encoding.UTF8.GetBytes("class A { }"));
                return Json(JsonSerializer.Serialize(new { content = body, encoding = "base64" }));
            }

            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                var tree = files.Select(x => new { path = x.Path, type = "blob", size = x.Size }).ToList();
                return Json(JsonSerializer.Serialize(new { tree, truncated = false }));
            }

            return Json(JsonSerializer.Serialize(new { default_branch = "main" }));
        }

        private static HttpResponseMessage Json(string json) =>
            new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }
}
