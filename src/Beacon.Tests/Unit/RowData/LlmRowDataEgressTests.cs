using System.Text.RegularExpressions;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Authorization;
using Beacon.Core.Models.Ai;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// The row-data egress log never stands between a request and the provider: a caller that cannot be resolved is recorded
/// as unknown (and the failure logged by type), a job that names itself is never looked up. And the convention behind it:
/// every class that sends prompts through an <see cref="ILlmProvider"/> either tracks them with
/// <see cref="ILlmRowDataEgress"/> or is listed here as sending no row data, with the reason.
/// </summary>
[TestFixture]
public class LlmRowDataEgressTests
{
    // Classes that call ILlmProvider.CompleteAsync without the egress log, and why no row data reaches their prompts.
    private static readonly Dictionary<string, string> RowFree = new(StringComparer.Ordinal)
    {
        ["src/Beacon.AI/Services/LlmProviders/LlmRequestQueue.cs"] = "the queue every request goes through (§6.1)",
        ["src/Beacon.AI/Services/LlmProviders/OpenAiProvider.cs"] = "an obsolete provider wrapper: forwards its caller's request unchanged",
        ["src/Beacon.AI/Services/LlmProviders/AzureOpenAiProvider.cs"] = "an obsolete provider wrapper: forwards its caller's request unchanged",
        ["src/Beacon.AI/Services/Mcp/SqlGenerationService.cs"] = "sends through the provider its caller hands in, which the ask pipeline and cross-source query track",
        ["src/Beacon.AI/Services/Mcp/IntentClassifier.cs"] = "the question only",
        ["src/Beacon.AI/Services/Mcp/DataSourceRouter.cs"] = "the question and the data sources' names and descriptions",
        ["src/Beacon.AI/Services/Ai/AiActor/AiActorService.cs"] = "schema, saved queries and execution counts, never rows"
    };

    private static readonly Regex CompletesAPrompt = new(@"\.CompleteAsync\(", RegexOptions.CultureInvariant);

    [Test]
    public async Task ACallerThatCannotBeResolved_IsRecordedAsUnknown_AndTheRequestIsSent()
    {
        var log = new LogRecorder();
        var actors = new Mock<IBeaconActorAccessor>();
        actors
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"no principal for {TestCredentials.ProviderSecretKey}"));
        var inner = Answering();
        var egress = new LlmRowDataEgress(actors.Object, log.For<LlmRowDataEgress>());

        var response = await egress.Track(inner.Object, new LlmRowDataScope(3, [5], [LlmRowDataCategories.SampleValues])).CompleteAsync(new LlmRequest());

        response.Content.Should().Be("ok");
        inner.Verify(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()), Times.Once);
        log.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.StartsWith("The caller of an LLM request carrying row data could not be resolved", StringComparison.Ordinal));
        log.Entries.Should().Contain(x => x.Level == LogLevel.Information && x.Message.EndsWith("was handed to the provider for caller unknown", StringComparison.Ordinal));
        log.Contains("sk_live").Should().BeFalse();
    }

    // A background job names itself, so its requests never depend on whom it runs as: the actor is not looked up.
    [Test]
    public async Task AJobThatNamesItself_IsNeverLookedUp()
    {
        var log = new LogRecorder();
        var actors = new Mock<IBeaconActorAccessor>(MockBehavior.Strict);
        var egress = new LlmRowDataEgress(actors.Object, log.For<LlmRowDataEgress>());

        await egress.Track(Answering().Object, new LlmRowDataScope(3, [5], [LlmRowDataCategories.ResultRows]) { CallerId = "eval-run" })
            .CompleteAsync(new LlmRequest());

        actors.Invocations.Should().BeEmpty();
        log.Entries.Should().ContainSingle().Which.Message.Should().EndWith("was handed to the provider for caller eval-run");
    }

    // The record is written as the request is handed over; a provider that then fails adds the outcome (type and code,
    // never the provider's message) and the failure still reaches the caller.
    [Test]
    public async Task ARequestTheProviderFails_IsRecordedAsHandedOver_ThenAsFailed()
    {
        var log = new LogRecorder();
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("upstream echoed Ivan Horvat"));
        var egress = new LlmRowDataEgress(Mock.Of<IBeaconActorAccessor>(MockBehavior.Strict), log.For<LlmRowDataEgress>());

        var act = () => egress.Track(inner.Object, new LlmRowDataScope(3, [5], [LlmRowDataCategories.ValueMatches]) { CallerId = "job" })
            .CompleteAsync(new LlmRequest());

        await act.Should().ThrowAsync<HttpRequestException>();
        log.Entries.Should().HaveCount(2).And.OnlyContain(x => x.EventId == Beacon.Core.Authorization.ProjectAccess.ProjectAccessEvents.RowDataSentToLlm);
        log.Entries[0].Level.Should().Be(LogLevel.Information);
        log.Entries[0].Message.Should().Be("An LLM request carrying value-matches read from data sources 5 of project 3 was handed to the provider for caller job");
        log.Entries[1].Level.Should().Be(LogLevel.Warning);
        log.Entries[1].Message.Should().StartWith("The LLM request carrying value-matches read from data sources 5 of project 3 for caller job failed at the provider (HttpRequestException");
        log.Contains("Horvat").Should().BeFalse("the provider's message is never logged");
    }

    // A cancelled request is not a provider failure: nothing beyond the hand-over record.
    [Test]
    public async Task ACancelledRequest_AddsNoFailureRecord()
    {
        var log = new LogRecorder();
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        var egress = new LlmRowDataEgress(Mock.Of<IBeaconActorAccessor>(MockBehavior.Strict), log.For<LlmRowDataEgress>());

        var act = () => egress.Track(inner.Object, new LlmRowDataScope(3, [5], [LlmRowDataCategories.ResultRows]) { CallerId = "job" })
            .CompleteAsync(new LlmRequest());

        await act.Should().ThrowAsync<OperationCanceledException>();
        log.Entries.Should().ContainSingle().Which.Level.Should().Be(LogLevel.Information);
    }

    [Test]
    public void EveryLlmProviderConsumer_TracksItsRowData_OrIsListedAsSendingNone()
    {
        var root = RepositoryRoot();
        var untracked = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(x => !x.Split(Path.DirectorySeparatorChar).Any(y => y is "bin" or "obj" or "Beacon.Tests"))
            .Select(x => (Path: Path.GetRelativePath(root, x).Replace(Path.DirectorySeparatorChar, '/'), Text: File.ReadAllText(x)))
            .Where(x => x.Text.Contains("ILlmProvider", StringComparison.Ordinal) && CompletesAPrompt.IsMatch(x.Text))
            .Where(x => !x.Text.Contains("ILlmRowDataEgress", StringComparison.Ordinal))
            .Select(x => x.Path)
            .Order(StringComparer.Ordinal)
            .ToList();

        untracked.Except(RowFree.Keys).Should().BeEmpty(
            "a class that sends prompts tracks its row data with ILlmRowDataEgress or is listed as row-free with the reason (found: {0})",
            string.Join(", ", untracked.Except(RowFree.Keys)));
        RowFree.Keys.Except(untracked).Should().BeEmpty("a listed class that no longer sends untracked prompts is taken off the list");
    }

    private static Mock<ILlmProvider> Answering()
    {
        var inner = new Mock<ILlmProvider>();
        inner
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "ok" });

        return inner;
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
