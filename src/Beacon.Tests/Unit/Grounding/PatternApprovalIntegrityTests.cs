using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Grounding;

/// <summary>
/// A decision binds to the content it was made on: the aggregation refresh never rewrites the content, question or example
/// SQL of a pattern a person approved or rejected (its statistics still refresh); an auto-approved pattern whose content
/// changes goes back to NeedsEvidence; an auto-approval checks the candidate's example SQL again first, like a person's
/// approval; and a run reads each data source's dialect and catalog once, through one batch of the grounding SQL check.
/// The detectors and the promotion step over in-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class PatternApprovalIntegrityTests
{
    private static readonly IReadOnlySet<int> NoCurators = new HashSet<int>();

    private const int ProjectId = 1;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;
    private const string MinedContent = "Common query pattern for public.orders (3 similar queries)";
    private const string MinedQuestion = "orders last week";
    private const string MinedSql = "SELECT id FROM orders";

    [TestCase(McpPatternStatus.Approved)]
    [TestCase(McpPatternStatus.Rejected)]
    public async Task APatternAPersonDecided_KeepsItsContent_AndOnlyItsStatisticsRefresh(McpPatternStatus status)
    {
        var reviewed = Pattern(status, "reviewed content", "reviewed question", "SELECT status FROM orders");
        var check = TestGroundingSqlChecks.PassThrough();
        var (service, context, _) = Build(check.Object, reviewed);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        reviewed.Should().BeEquivalentTo(
            new
            {
                Status = status,
                PatternContent = "reviewed content",
                ExampleQuestion = "reviewed question",
                ExampleSql = "SELECT status FROM orders",
                SignalCount = 3
            },
            x => x.ExcludingMissingMembers());
        reviewed.LastRefreshedAt.Should().NotBeNull();
        check.Verify(x => x.CheckAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never, "nothing mined is stored on it");
    }

    [Test]
    public async Task AnAutoApprovedPatternWhoseContentChanges_GoesBackToNeedsEvidence_WithTheNewContent()
    {
        var autoApproved = Pattern(McpPatternStatus.AutoApproved, "Common query pattern for public.orders (2 similar queries)", MinedQuestion, "SELECT total FROM orders");
        var (service, context, _) = Build(TestGroundingSqlChecks.PassThrough().Object, autoApproved);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        autoApproved.Status.Should().Be(McpPatternStatus.NeedsEvidence, "the replay measured other content");
        autoApproved.PatternContent.Should().Be(MinedContent);
        autoApproved.ExampleSql.Should().Be(MinedSql);
    }

    [Test]
    public async Task AnAutoApprovedPatternWhoseContentIsUnchanged_StaysAutoApproved()
    {
        var autoApproved = Pattern(McpPatternStatus.AutoApproved, MinedContent, MinedQuestion, MinedSql);
        var (service, context, _) = Build(TestGroundingSqlChecks.PassThrough().Object, autoApproved);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        autoApproved.Status.Should().Be(McpPatternStatus.AutoApproved);
        autoApproved.SignalCount.Should().Be(3);
    }

    [Test]
    public async Task APatternAwaitingEvidence_TakesTheNewContent_AndKeepsItsStatus()
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, "older content", "older question", "SELECT total FROM orders");
        var (service, context, _) = Build(TestGroundingSqlChecks.PassThrough().Object, candidate);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        candidate.Should().BeEquivalentTo(
            new { Status = McpPatternStatus.NeedsEvidence, PatternContent = MinedContent, ExampleQuestion = MinedQuestion, ExampleSql = MinedSql },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AnAutoApproval_ChecksTheExampleSqlAgain_AndStoresItWithoutComments()
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, "SELECT id FROM orders /* the usual */");
        var verifier = Verifier(passed: true);
        string? replayedSql = null;
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback((McpLearnedPattern x, int _, CancellationToken _) => replayedSql = x.ExampleSql)
            .ReturnsAsync(Verdict(passed: true));
        var (service, context, _) = Build(TestGroundingSqlChecks.Real(), candidate, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.AutoApproved);
        candidate.ExampleSql.Should().Be("SELECT id FROM orders");
        replayedSql.Should().Be("SELECT id FROM orders", "the replay measures the SQL a prompt will show");
    }

    // A candidate without example SQL has none to check: the replay alone decides, and nothing counts as blocked.
    [TestCase(null)]
    [TestCase("")]
    public async Task ACandidateWithoutExampleSql_IsReplayedUnchecked_AndAutoApproved(string? exampleSql)
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, exampleSql!);
        var reads = 0;
        var (service, context, _) = Build(TestGroundingSqlChecks.Real(onRead: () => reads++), candidate, Verifier(passed: true).Object);
        var stats = new McpLearningAggregationService.ExtractionStats();

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), stats, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.AutoApproved);
        candidate.ExampleSql.Should().Be(exampleSql);
        stats.BlockedPromotions.Should().Be(0);
        reads.Should().Be(0, "no SQL is checked, so no data source or catalog is read");
    }

    [Test]
    public async Task ACandidateWhoseExampleSqlTheCheckRefuses_IsNotReplayed_CountedAndLoggedByIdsOnly()
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, "SELECT secret_column FROM orders");
        var verifier = Verifier(passed: true);
        var (service, context, logs) = Build(TestGroundingSqlChecks.Real(), candidate, verifier.Object);
        var stats = new McpLearningAggregationService.ExtractionStats();

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), stats, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.ExampleSql.Should().Be("SELECT secret_column FROM orders");
        verifier.VerifyNoOtherCalls();
        stats.BlockedPromotions.Should().Be(1);
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning)
            .Which.Message.Should().Be($"Learned pattern {candidate.Id} of project {ProjectId} was not replayed for auto-approval: its example SQL did not pass the SQL check");
        logs.Contains("secret_column").Should().BeFalse("no SQL reaches the log");
    }

    [Test]
    public async Task TheRunsBatch_ChecksMinedAndPromotedSql_InsteadOfTheServicesCheck()
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, MinedSql);
        var serviceCheck = new Mock<IGroundingSqlCheck>(MockBehavior.Strict);
        var batch = TestGroundingSqlChecks.PassThrough();
        var (service, context, _) = Build(serviceCheck.Object, candidate, Verifier(passed: true).Object);
        var stats = new McpLearningAggregationService.ExtractionStats { SqlCheck = batch.Object };

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, stats, CancellationToken.None);
        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), stats, CancellationToken.None);

        batch.Verify(x => x.CheckAsync(DataSourceId, MinedSql, It.IsAny<CancellationToken>()), Times.Exactly(2), "once mined, once before the auto-approval");
        candidate.Status.Should().Be(McpPatternStatus.AutoApproved);
    }

    [Test]
    public async Task AnAggregationRun_StartsOneBatchOfTheCheck()
    {
        var serviceCheck = new Mock<IGroundingSqlCheck>(MockBehavior.Strict);
        var batch = TestGroundingSqlChecks.PassThrough();
        serviceCheck
            .Setup(x => x.StartBatch())
            .Returns(batch.Object);
        var signals = Signals();
        foreach (var signal in signals)
        {
            signal.CreatedTime = DateTime.UtcNow;
        }

        var patterns = new List<McpLearnedPattern>();
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(signals, saved),
                    [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(patterns, saved)
                },
                saved));
        var service = new McpLearningAggregationService(
            factory.Object,
            SettingsProviderMock.Create(new McpSettingsData { EnableReplayVerification = false }).Object,
            serviceCheck.Object,
            new LogRecorder().For<McpLearningAggregationService>());

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        serviceCheck.Verify(x => x.StartBatch(), Times.Once);
        batch.Verify(x => x.CheckAsync(DataSourceId, MinedSql, It.IsAny<CancellationToken>()), Times.Once);
        saved.OfType<McpLearnedPattern>().Should().ContainSingle().Which.ExampleSql.Should().Be(MinedSql);
    }

    private static McpLearnedPattern Pattern(McpPatternStatus status, string content, string question, string sql)
    {
        return new McpLearnedPattern
        {
            Id = 31,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.CommonQuery,
            PatternContent = content,
            ExampleQuestion = question,
            ExampleSql = sql,
            SignalCount = 1,
            Confidence = 0.5,
            Status = status
        };
    }

    private static List<McpQuerySignal> Signals()
    {
        return Enumerable.Range(0, 3)
            .Select(_ =>
                new McpQuerySignal
                {
                    Tool = "ask",
                    Question = MinedQuestion,
                    ProjectId = ProjectId,
                    DataSourceId = DataSourceId,
                    GeneratedSql = MinedSql,
                    TablesUsed = "[\"public.orders\"]",
                    IsSuccessful = true
                })
            .ToList();
    }

    private static McpSettingsData ReplaySettings()
    {
        return new McpSettingsData
        {
            EnableLearning = true,
            EnableReplayVerification = true,
            LearningReplayMinFlips = 1
        };
    }

    private static Mock<IPatternReplayVerifier> Verifier(bool passed)
    {
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Verdict(passed));

        return verifier;
    }

    private static ReplayVerdict Verdict(bool passed)
    {
        return new ReplayVerdict(RelevantCases: 1, BaselineFailing: 1, Flipped: 1, Regressions: 0, Errored: 0, Passed: passed);
    }

    private static (McpLearningAggregationService Service, BeaconContext Context, LogRecorder Logs) Build(
        IGroundingSqlCheck check,
        McpLearnedPattern existing,
        IPatternReplayVerifier? verifier = null)
    {
        var saved = new List<object>();
        var context = new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet([existing], saved)
            },
            saved);
        var logs = new LogRecorder();
        var service = new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            check,
            logs.For<McpLearningAggregationService>(),
            lessonExtractor: null,
            replayVerifier: verifier);

        return (service, context, logs);
    }
}
