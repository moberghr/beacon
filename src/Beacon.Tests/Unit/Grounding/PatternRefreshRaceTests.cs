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
/// The aggregation writes a pattern only while it still holds what was read: a refresh never overwrites a person's decision
/// made between its read and its write; an auto-approval is written only from NeedsEvidence, and only for the content the
/// replay measured. A refresh compares content with its counts left out and SQL without comments, so a count that moves —
/// or SQL stored before comments were removed — keeps an auto-approved pattern approved; a real change sends it back to
/// NeedsEvidence, counted per run and logged once. The detectors and the promotion step over in-memory sets, no database
/// (§4.7); a concurrent change is made from inside the step that runs between the read and the write.
/// </summary>
[TestFixture]
public class PatternRefreshRaceTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = TestGroundingSqlChecks.DataSourceId;
    private const string MinedContent = "Common query pattern for public.orders (3 similar queries)";
    private const string MinedQuestion = "orders last week";
    private const string MinedSql = "SELECT id FROM orders";

    private static readonly IReadOnlySet<int> NoCurators = new HashSet<int>();

    // The example SQL is checked between the refresh's read and its write: a person decides the pattern right then.
    [TestCase(McpPatternStatus.Approved)]
    [TestCase(McpPatternStatus.Rejected)]
    public async Task ADecisionBetweenARefreshsReadAndItsWrite_IsNeverOverwritten(McpPatternStatus decided)
    {
        var pattern = Pattern(McpPatternStatus.NeedsEvidence, "older content", MinedQuestion, "SELECT status FROM orders");
        var check = new Mock<IGroundingSqlCheck>();
        check
            .Setup(x => x.CheckAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback(() => pattern.Status = decided)
            .ReturnsAsync((int _, string sql, CancellationToken _) => GroundingSqlCheckResult.Allowed(sql));
        var (service, context, _) = Build(check.Object, pattern);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        pattern.Should().BeEquivalentTo(
            new { Status = decided, PatternContent = "older content", ExampleSql = "SELECT status FROM orders", SignalCount = 1 },
            x => x.ExcludingMissingMembers(),
            "the refresh read NeedsEvidence and writes only while the pattern still is");
    }

    // A person decides the candidate while the replay measures it: an auto-approval is written only from NeedsEvidence.
    [TestCase(McpPatternStatus.Approved)]
    [TestCase(McpPatternStatus.Rejected)]
    [TestCase(McpPatternStatus.Pending)]
    public async Task ADecisionDuringTheReplay_IsNeverTurnedIntoAnAutoApproval(McpPatternStatus decided)
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, MinedSql);
        var verifier = ReplayThat(() => candidate.Status = decided);
        var (service, context, _) = Build(TestGroundingSqlChecks.PassThrough().Object, candidate, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(decided);
        candidate.LastVerifiedAt.Should().BeNull();
    }

    // A refresh lands while the replay measures the candidate: the replay measured other content, so no auto-approval.
    [Test]
    public async Task ARefreshDuringTheReplay_LeavesTheCandidateForTheNextMeasurement()
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, MinedSql);
        var verifier = ReplayThat(() => candidate.ExampleSql = "SELECT status FROM orders");
        var (service, context, _) = Build(TestGroundingSqlChecks.PassThrough().Object, candidate, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.ExampleSql.Should().Be("SELECT status FROM orders");
    }

    [Test]
    public async Task AMeasuredUnchangedCandidate_IsAutoApproved()
    {
        var candidate = Pattern(McpPatternStatus.NeedsEvidence, MinedContent, MinedQuestion, MinedSql);
        var (service, context, _) = Build(TestGroundingSqlChecks.PassThrough().Object, candidate, ReplayThat(() => { }).Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, ReplaySettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.AutoApproved);
        candidate.LastVerifiedAt.Should().NotBeNull();
    }

    // Counts move every run, and SQL stored before comments were removed differs only by them: neither is a change, so the
    // pattern stays auto-approved (no replay to pay for), and its refreshed content is stored.
    [TestCase("Common query pattern for public.orders (12 similar queries)", MinedSql)]
    [TestCase(MinedContent, "-- the usual\nSELECT id FROM orders")]
    [TestCase(MinedContent, "SELECT id /* the ids */ FROM orders")]
    [TestCase(MinedContent, "  SELECT id FROM orders  ")]
    public async Task AnAutoApprovedPattern_WhoseContentDiffersOnlyByCountsOrComments_StaysAutoApproved(string storedContent, string storedSql)
    {
        var pattern = Pattern(McpPatternStatus.AutoApproved, storedContent, MinedQuestion, storedSql);
        var stats = new McpLearningAggregationService.ExtractionStats();
        var (service, context, _) = Build(TestGroundingSqlChecks.Real(), pattern);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, stats, CancellationToken.None);

        pattern.Should().BeEquivalentTo(
            new { Status = McpPatternStatus.AutoApproved, PatternContent = MinedContent, ExampleSql = MinedSql, SignalCount = 3 },
            x => x.ExcludingMissingMembers());
        stats.Demotions.Should().Be(0);
    }

    [TestCase("Tables public.orders+public.customers are frequently joined together (3 queries)", MinedQuestion, "SELECT status FROM orders")]
    [TestCase(MinedContent, "orders this week", MinedSql)]
    [TestCase("Common query pattern for public.customers (3 similar queries)", MinedQuestion, MinedSql)]
    public async Task AnAutoApprovedPattern_WhoseContentChanges_GoesBackToNeedsEvidence_AndIsCounted(string storedContent, string storedQuestion, string storedSql)
    {
        var pattern = Pattern(McpPatternStatus.AutoApproved, storedContent, storedQuestion, storedSql);
        var stats = new McpLearningAggregationService.ExtractionStats();
        var (service, context, _) = Build(TestGroundingSqlChecks.Real(), pattern);

        await service.DetectCommonQueriesAsync(context, ProjectId, DataSourceId, Signals(), NoCurators, stats, CancellationToken.None);

        pattern.Should().BeEquivalentTo(
            new { Status = McpPatternStatus.NeedsEvidence, PatternContent = MinedContent, ExampleQuestion = MinedQuestion, ExampleSql = MinedSql },
            x => x.ExcludingMissingMembers());
        stats.Demotions.Should().Be(1);
    }

    // The demotions of a run are logged once, at its end, as a count.
    [Test]
    public async Task AnAggregationRun_LogsItsDemotionsOnce()
    {
        var signals = Signals();
        foreach (var signal in signals)
        {
            signal.CreatedTime = DateTime.UtcNow;
        }

        var patterns = new List<McpLearnedPattern> { Pattern(McpPatternStatus.AutoApproved, MinedContent, MinedQuestion, "SELECT status FROM orders") };
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
        var logs = new LogRecorder();
        var service = new McpLearningAggregationService(
            factory.Object,
            SettingsProviderMock.Create(new McpSettingsData { EnableReplayVerification = false }).Object,
            TestGroundingSqlChecks.PassThrough().Object,
            logs.For<McpLearningAggregationService>());

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        patterns[0].Status.Should().Be(McpPatternStatus.NeedsEvidence);
        logs.Entries.Should().ContainSingle(x => x.Message.Contains("went back to NeedsEvidence"))
            .Which.Should().BeEquivalentTo(
                new { Level = LogLevel.Information, Message = "1 auto-approved learned pattern(s) went back to NeedsEvidence this run: their content changed" },
                x => x.ExcludingMissingMembers());
    }

    private static Mock<IPatternReplayVerifier> ReplayThat(Action duringTheReplay)
    {
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(duringTheReplay)
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 1, BaselineFailing: 1, Flipped: 1, Regressions: 0, Errored: 0, Passed: true));

        return verifier;
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
