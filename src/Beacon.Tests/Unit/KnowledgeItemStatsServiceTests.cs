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
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC1: <see cref="KnowledgeItemStatsService"/> gives every knowledge item the project's <c>ask</c> signals in the retention
/// window carried a stats row, from the project's own signals only (§1.12), updating stored rows, deleting the rows of
/// items no signal carried, in one save (§5.7). And the learning job recomputes each project's counters after its
/// aggregation; a failure there is logged by ids only and does not fail the aggregation, the caller's cancellation still
/// unwinds it. Driven through <c>TestAsyncQueryable</c>-backed sets (§4.7).
/// </summary>
[TestFixture]
public class KnowledgeItemStatsServiceTests
{
    private const int ProjectId = 1;
    private const int OtherProjectId = 2;

    private static readonly DateTimeOffset Now = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly KnowledgeItemKey Golden = new(McpKnowledgeItemKind.GoldenCase, 7);
    private static readonly KnowledgeItemKey Pattern = new(McpKnowledgeItemKind.LearnedPattern, 8);
    private static readonly KnowledgeItemKey Term = new(McpKnowledgeItemKind.GlossaryTerm, 9);

    private List<McpQuerySignal> _signals = null!;
    private List<McpKnowledgeItemStat> _stats = null!;
    private List<object> _saved = null!;
    private int _saves;

    [SetUp]
    public void SetUp()
    {
        _signals = [];
        _stats = [];
        _saved = [];
        _saves = 0;
    }

    [Test]
    public async Task Recompute_WritesARowPerItemTheProjectsAskSignalsCarried()
    {
        _signals.Add(Ask(1, verdict: McpUserVerdict.Correct, callerHash: "caller-a", items: [Golden, Pattern]));
        _signals.Add(Ask(2, callerHash: "caller-b", items: [Golden]));

        await BuildService().RecomputeAsync(ProjectId, CancellationToken.None);

        var rows = AddedRows();
        rows.Should().HaveCount(2);
        var golden = rows.Single(x => x.Kind == Golden.Kind);
        golden.ProjectId.Should().Be(ProjectId);
        golden.ItemId.Should().Be(Golden.ItemId);
        golden.HelpfulScore.Should().Be(1.25);
        golden.HarmfulScore.Should().Be(0);
        golden.SignalCount.Should().Be(2);
        golden.CallerCount.Should().Be(2);
        golden.ComputedAt.Should().Be(Now.UtcDateTime);
        var pattern = rows.Single(x => x.Kind == Pattern.Kind);
        pattern.ItemId.Should().Be(Pattern.ItemId);
        pattern.HelpfulScore.Should().Be(1);
        pattern.SignalCount.Should().Be(1);
        pattern.CallerCount.Should().Be(1);
        _saves.Should().Be(1);
    }

    [Test]
    public async Task Recompute_AnotherProjectsSignals_NeverCount()
    {
        _signals.Add(Ask(1, callerHash: "caller-a", items: [Golden]));
        _signals.Add(Ask(2, projectId: OtherProjectId, verdict: McpUserVerdict.Incorrect, fromCurator: true, callerHash: "caller-b", items: [Golden, Term]));

        await BuildService().RecomputeAsync(ProjectId, CancellationToken.None);

        var row = AddedRows().Should().ContainSingle().Subject;
        row.ProjectId.Should().Be(ProjectId);
        row.Kind.Should().Be(Golden.Kind);
        row.HelpfulScore.Should().Be(0.25);
        row.HarmfulScore.Should().Be(0, "the other project's curator verdict is not this project's");
        row.SignalCount.Should().Be(1);
        row.CallerCount.Should().Be(1);
    }

    [Test]
    public async Task Recompute_TheWindow_IsTheProjectsEffectiveRetention()
    {
        _signals.Add(Ask(1, ageDays: 29, items: [Golden]));
        _signals.Add(Ask(2, ageDays: 31, items: [Golden, Term]));

        await BuildService(projectRetentionDays: 30).RecomputeAsync(ProjectId, CancellationToken.None);

        var row = AddedRows().Should().ContainSingle().Subject;
        row.Kind.Should().Be(Golden.Kind);
        row.SignalCount.Should().Be(1);
    }

    [Test]
    public async Task Recompute_AProjectWindowBelowOneDay_FallsBackToTheGlobalWindow()
    {
        _signals.Add(Ask(1, ageDays: 9, items: [Golden]));
        _signals.Add(Ask(2, ageDays: 11, items: [Term]));

        await BuildService(projectRetentionDays: 0, globalRetentionDays: 10).RecomputeAsync(ProjectId, CancellationToken.None);

        AddedRows().Should().ContainSingle().Which.Kind.Should().Be(Golden.Kind);
    }

    [TestCase("query")]
    [TestCase("dry_run")]
    public async Task Recompute_OnlyAskSignalsCount(string tool)
    {
        var signal = Ask(1, items: [Golden]);
        signal.Tool = tool;
        _signals.Add(signal);

        await BuildService().RecomputeAsync(ProjectId, CancellationToken.None);

        AddedRows().Should().BeEmpty();
    }

    [TestCase("query", true, ProjectId, 0, 0.25)]
    [TestCase("dry_run", true, ProjectId, 0, 0.25)]
    [TestCase("query", false, ProjectId, 0.25, 0)]
    [TestCase("query", true, OtherProjectId, 0.25, 0)]
    [TestCase("ask", true, ProjectId, 0.25, 0)]
    public async Task Recompute_AnUnratedAnswer_IsHarmfulOnlyWhenTheProjectsQueryOrDryRunReranItEdited(
        string childTool, bool edited, int childProjectId, double helpful, double harmful)
    {
        _signals.Add(Ask(1, items: [Golden]));
        _signals.Add(new McpQuerySignal
        {
            Id = 2,
            ProjectId = childProjectId,
            Tool = childTool,
            Question = "select 1",
            CreatedTime = Now.UtcDateTime,
            ParentSignalId = 1,
            IsEditedRerun = edited,
            IsSuccessful = true
        });

        await BuildService().RecomputeAsync(ProjectId, CancellationToken.None);

        var row = AddedRows().Should().ContainSingle().Subject;
        row.HelpfulScore.Should().Be(helpful);
        row.HarmfulScore.Should().Be(harmful);
        row.SignalCount.Should().Be(1, "the re-run is not a signal that carried the item");
    }

    [Test]
    public async Task Recompute_UpdatesStoredRows_DeletesRowsOfItemsNoSignalCarried_AndLeavesOtherProjectsRows()
    {
        var storedGolden = Stat(ProjectId, Golden);
        var stalePattern = Stat(ProjectId, new KnowledgeItemKey(McpKnowledgeItemKind.LearnedPattern, 99));
        var otherProjects = Stat(OtherProjectId, Golden);
        _stats.AddRange([storedGolden, stalePattern, otherProjects]);
        _signals.Add(Ask(1, verdict: McpUserVerdict.Incorrect, callerHash: "caller-a", items: [Golden, Pattern]));

        await BuildService().RecomputeAsync(ProjectId, CancellationToken.None);

        _stats.Should().Equal([storedGolden, otherProjects], "the stale row is deleted and the others are the same objects");
        storedGolden.HelpfulScore.Should().Be(0);
        storedGolden.HarmfulScore.Should().Be(1);
        storedGolden.SignalCount.Should().Be(1);
        storedGolden.CallerCount.Should().Be(1);
        storedGolden.ComputedAt.Should().Be(Now.UtcDateTime);
        otherProjects.HelpfulScore.Should().Be(50, "another project's row is never touched");
        otherProjects.ComputedAt.Should().Be(Now.UtcDateTime.AddDays(-3));
        var added = AddedRows().Should().ContainSingle().Subject;
        added.Kind.Should().Be(Pattern.Kind);
        added.ItemId.Should().Be(Pattern.ItemId);
        added.HarmfulScore.Should().Be(1);
        _saves.Should().Be(1);
    }

    [Test]
    public async Task Recompute_WithoutSignalsInTheWindow_DeletesTheProjectsRows()
    {
        var stored = Stat(ProjectId, Golden);
        var otherProjects = Stat(OtherProjectId, Golden);
        _stats.AddRange([stored, otherProjects]);
        _signals.Add(Ask(1, ageDays: 200, items: [Golden]));

        await BuildService().RecomputeAsync(ProjectId, CancellationToken.None);

        _stats.Should().ContainSingle().Which.Should().BeSameAs(otherProjects);
        AddedRows().Should().BeEmpty();
        _saves.Should().Be(1);
    }

    [Test]
    public async Task AggregateLearnedPatterns_RecomputesTheStatsOfEveryProjectWithLearningOn()
    {
        _signals.Add(Ask(1, items: [Golden]));
        _signals.Add(Ask(2, projectId: OtherProjectId, items: [Golden]));
        _signals.Add(Ask(3, projectId: 3, items: [Golden]));
        var stats = new Mock<IKnowledgeItemStatsService>();
        var settings = SettingsProviderMock.Create(projectSettings: new Dictionary<int, McpSettingsData>
        {
            [3] = new() { EnableLearning = false }
        });

        await BuildJob(stats.Object, new LogRecorder(), settings.Object).AggregateLearnedPatternsAsync(CancellationToken.None);

        stats.Verify(x => x.RecomputeAsync(ProjectId, It.IsAny<CancellationToken>()), Times.Once);
        stats.Verify(x => x.RecomputeAsync(OtherProjectId, It.IsAny<CancellationToken>()), Times.Once);
        stats.Verify(x => x.RecomputeAsync(3, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AggregateLearnedPatterns_ARecomputeFailure_IsAWarningByIdsOnly_AndTheRunGoesOn()
    {
        _signals.Add(Ask(1, items: [Golden]));
        _signals.Add(Ask(2, projectId: OtherProjectId, items: [Golden]));
        var stats = new Mock<IKnowledgeItemStatsService>();
        stats
            .Setup(x => x.RecomputeAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SELECT secret FROM orders"));
        var logs = new LogRecorder();

        var run = () => BuildJob(stats.Object, logs).AggregateLearnedPatternsAsync(CancellationToken.None);

        await run.Should().NotThrowAsync();
        stats.Verify(x => x.RecomputeAsync(OtherProjectId, It.IsAny<CancellationToken>()), Times.Once);
        var warning = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        warning.Message.Should().Contain("knowledge item stats").And.Contain($"project {ProjectId}");
        warning.Exception.Should().BeNull();
        logs.Contains("secret").Should().BeFalse("the failure is logged by its code, never its message");
        logs.Contains("Failed to aggregate patterns").Should().BeFalse("the pattern aggregation did not fail");
    }

    [Test]
    public async Task AggregateLearnedPatterns_CancelledDuringARecompute_Unwinds()
    {
        _signals.Add(Ask(1, items: [Golden]));
        _signals.Add(Ask(2, projectId: OtherProjectId, items: [Golden]));
        using var cancellation = new CancellationTokenSource();
        var stats = new Mock<IKnowledgeItemStatsService>();
        stats
            .Setup(x => x.RecomputeAsync(ProjectId, It.IsAny<CancellationToken>()))
            .Returns(async (int _, CancellationToken ct) =>
            {
                await cancellation.CancelAsync();
                ct.ThrowIfCancellationRequested();
            });
        var logs = new LogRecorder();

        var run = () => BuildJob(stats.Object, logs).AggregateLearnedPatternsAsync(cancellation.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        stats.Verify(x => x.RecomputeAsync(OtherProjectId, It.IsAny<CancellationToken>()), Times.Never);
        logs.Entries.Should().NotContain(x => x.Level == LogLevel.Warning);
    }

    private KnowledgeItemStatsService BuildService(int projectRetentionDays = 90, int globalRetentionDays = 90)
    {
        var settings = SettingsProviderMock.Create(
            new McpSettingsData { LearningSignalRetentionDays = globalRetentionDays },
            projectSettings: new Dictionary<int, McpSettingsData>
            {
                [ProjectId] = new() { LearningSignalRetentionDays = projectRetentionDays }
            });

        return new KnowledgeItemStatsService(Factory(), settings.Object, new FakeTimeProvider(Now));
    }

    private McpLearningAggregationService BuildJob(
        IKnowledgeItemStatsService stats, LogRecorder logs, IMcpSettingsProvider? settings = null)
    {
        return new McpLearningAggregationService(
            Factory(),
            settings ?? SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            logs.For<McpLearningAggregationService>(),
            lessonExtractor: null,
            replayVerifier: null,
            knowledgeItemStats: stats);
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(McpQuerySignal)] = RecordingBeaconContext.MemorySet(_signals, _saved),
            [typeof(McpKnowledgeItemStat)] = RecordingBeaconContext.MemorySet(_stats, _saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved, onSave: () => _saves++));

        return factory.Object;
    }

    private List<McpKnowledgeItemStat> AddedRows()
    {
        return _saved
            .OfType<McpKnowledgeItemStat>()
            .ToList();
    }

    private static McpQuerySignal Ask(
        int id,
        int projectId = ProjectId,
        McpUserVerdict verdict = McpUserVerdict.Unset,
        bool fromCurator = false,
        string? callerHash = null,
        int ageDays = 1,
        KnowledgeItemKey[]? items = null)
    {
        var signal = new McpQuerySignal
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = 1,
            Tool = "ask",
            Question = "how many orders shipped",
            CreatedTime = Now.UtcDateTime.AddDays(-ageDays),
            CallerHash = callerHash,
            UserVerdict = verdict,
            FeedbackFromCurator = fromCurator,
            IsSuccessful = true
        };
        signal.ContextItems = (items ?? [])
            .Select(x =>
                new McpSignalContextItem
                {
                    SignalId = id,
                    Kind = x.Kind,
                    ItemId = x.ItemId,
                    Rank = 1,
                    Signal = signal
                })
            .ToList();

        return signal;
    }

    private static McpKnowledgeItemStat Stat(int projectId, KnowledgeItemKey item)
    {
        return new McpKnowledgeItemStat
        {
            ProjectId = projectId,
            Kind = item.Kind,
            ItemId = item.ItemId,
            HelpfulScore = 50,
            HarmfulScore = 50,
            SignalCount = 50,
            CallerCount = 50,
            ComputedAt = Now.UtcDateTime.AddDays(-3)
        };
    }
}
