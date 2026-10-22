using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Schema-correction detection is LLM-PRIMARY with a deterministic regex fallback (§ Architecture ⑦), and
/// — critically — a NEW candidate is ALWAYS created NeedsEvidence, never AutoApproved on confidence alone
/// (the headline safety claim, § Architecture ⑥). This test drives
/// <see cref="McpLearningAggregationService.DetectSchemaCorrectionsAsync"/> in isolation over a mocked
/// <see cref="BeaconContext"/> (async-queryable doubles, no DB — §4.7) with an extractor that returns
/// <c>null</c>, proving the LLM-primary path is TRIED then the regex fallback fires AND the resulting
/// pattern still lands in NeedsEvidence even though its computed confidence exceeds the old 0.7 threshold.
/// </summary>
[TestFixture]
public class McpLearningDetectionTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = 1;

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ExtractorReturnsNull_FallsBackToRegex_AndCreatesNeedsEvidenceNotAutoApproved()
    {
        // LLM-primary path is available but yields nothing usable (null) for this cluster.
        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        extractor
            .Setup(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExtractedLesson?)null);

        var captured = new List<McpLearnedPattern>();
        var (service, context) = BuildService(extractor.Object, captured);

        // One (dataSource, public.orders, created_at→created_on) cluster with 3 signals so the count-based
        // confidence (0.5 + 3 × 0.15 = 0.95) comfortably exceeds the OLD 0.7 auto-approve threshold.
        var signals = new List<McpQuerySignal>
        {
            CorrectionSignal(),
            CorrectionSignal(),
            CorrectionSignal()
        };

        var extraction = new McpLearningAggregationService.ExtractionStats();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, signals, extraction, retainContent: true, CancellationToken.None);

        // (a) Exactly one candidate was created, and it is NeedsEvidence — NEVER AutoApproved on confidence.
        captured.Should().ContainSingle();
        var pattern = captured[0];
        pattern.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        pattern.Status.Should().NotBe(McpPatternStatus.AutoApproved);
        pattern.Confidence.Should().BeGreaterThan(0.7,
            "the count-based confidence exceeds the old auto-approve threshold yet must NOT auto-approve");

        // (b) Content came from the deterministic REGEX fallback (extractor returned null), so it is the
        // exact regex-produced wrong→correct column mapping — not any LLM-authored lesson text.
        pattern.PatternType.Should().Be(McpPatternType.SchemaCorrection);
        pattern.PatternContent.Should().Be(
            "NEVER use 'created_at' on public.orders — correct column is 'created_on'");
        pattern.SchemaName.Should().Be("public");
        pattern.TableName.Should().Be("orders");
        pattern.ColumnName.Should().Be("created_at");

        // (c) The LLM-primary path was attempted once then produced nothing, proving try-LLM-then-fall-back.
        extraction.Attempts.Should().Be(1);
        extraction.NullResults.Should().Be(1);
        extractor.Verify(
            x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AggregateLearnedPatternsAsync_DryRunSignals_AreNeverMined()
    {
        // R6-3: dry_run signals carry SQL in Question (not natural language) and land
        // IsSuccessful=false on gate rejections without any execution — the aggregation query must
        // keep them OUT of pattern mining entirely (they stay in the signals table for other
        // analytics). The ask cluster is the control proving the pipeline ran end-to-end; the
        // dry_run cluster targets a DIFFERENT table so any leak is unambiguous.
        var signals = new List<McpQuerySignal>
        {
            CorrectionSignal(),
            CorrectionSignal(),
            CorrectionSignal(),
            DryRunCorrectionSignal(),
            DryRunCorrectionSignal(),
            DryRunCorrectionSignal()
        };

        var captured = new List<McpLearnedPattern>();
        var service = BuildAggregationService(signals, captured);

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        // Control: the ask cluster WAS mined — so the absence below is the filter, not a dead run.
        captured.Should().Contain(x =>
            x.PatternType == McpPatternType.SchemaCorrection && x.ColumnName == "created_at");

        // The dry_run cluster (public.tickets / status_x) left no trace in ANY detector.
        captured.Should().NotContain(x => x.TableName == "tickets");
        captured.Should().NotContain(x => x.ColumnName == "status_x");
    }

    [Test]
    public async Task AggregateLearnedPatternsAsync_ProjectWithLearningOff_IsSkipped_WhileOthersAreMined()
    {
        // T-F003: the per-project effective EnableLearning is the inner gate. Project 1 inherits the global ON;
        // project 2 overrides to OFF — its (otherwise mineable) invoices cluster must leave no pattern.
        var signals = new List<McpQuerySignal>
        {
            CorrectionSignal(),
            CorrectionSignal(),
            CorrectionSignal(),
            CorrectionSignalFor(projectId: 2, table: "public.invoices", badColumn: "amount_x", goodColumn: "amount"),
            CorrectionSignalFor(projectId: 2, table: "public.invoices", badColumn: "amount_x", goodColumn: "amount"),
            CorrectionSignalFor(projectId: 2, table: "public.invoices", badColumn: "amount_x", goodColumn: "amount")
        };
        var captured = new List<McpLearnedPattern>();
        var service = BuildAggregationService(signals, captured, perProject: new Dictionary<int, McpSettingsData>
        {
            [2] = new McpSettingsData { EnableLearning = false, LearningSignalRetentionDays = 90 }
        });

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        captured.Should().Contain(x => x.PatternType == McpPatternType.SchemaCorrection && x.ColumnName == "created_at",
            "project 1 (learning on) is still mined — proves the run happened");
        captured.Should().NotContain(x => x.TableName == "invoices" || x.ColumnName == "amount_x",
            "project 2 has learning switched off via its effective settings");
    }

    [Test]
    public async Task AggregateLearnedPatternsAsync_ProjectWithRetainQueryContentFalse_SkipsContentBearingDetectorsAndTheLessonExtractor()
    {
        // SC5: a project whose effective RetainQueryContent is false must still mine SchemaCorrection
        // (via the deterministic template) and JoinPattern (structural: table pair + count) but must
        // skip CommonQuery and DocumentationGap ENTIRELY, and must never invoke the LLM lesson extractor
        // (its FailureCluster/lesson are free text — § Registry classification).
        const int LockedProjectId = 3;

        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        extractor
            .Setup(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractedLesson(
                McpPatternType.SchemaCorrection,
                "LLM-authored free-text lesson that must never be persisted under the lock",
                "an LLM-authored example question",
                "SELECT 1 -- LLM-authored example sql",
                "symptom", "root cause", "rule", null, null));

        var signals = new List<McpQuerySignal>
        {
            // Schema corrections: 3-signal cluster on public.orders (also, incidentally, a 3-signal
            // same-table cluster that WOULD satisfy CommonQuery's threshold too, proving the skip is a
            // real gate and not just an absence of eligible data).
            CorrectionSignalFor(LockedProjectId, "public.orders", "created_at", "created_on"),
            CorrectionSignalFor(LockedProjectId, "public.orders", "created_at", "created_on"),
            CorrectionSignalFor(LockedProjectId, "public.orders", "created_at", "created_on"),

            // Join pattern: 2-signal cluster joining public.orders + public.customers.
            JoinSignal(LockedProjectId),
            JoinSignal(LockedProjectId),

            // Documentation gap: 5-signal cluster on public.gaps with a 40% error rate.
            GapSignal(LockedProjectId, isSuccessful: false),
            GapSignal(LockedProjectId, isSuccessful: false),
            GapSignal(LockedProjectId, isSuccessful: true),
            GapSignal(LockedProjectId, isSuccessful: true),
            GapSignal(LockedProjectId, isSuccessful: true)
        };

        var captured = new List<McpLearnedPattern>();
        var service = BuildAggregationService(
            signals,
            captured,
            perProject: new Dictionary<int, McpSettingsData>
            {
                [LockedProjectId] = new McpSettingsData
                {
                    EnableLearning = true,
                    RetainQueryContent = false,
                    LearningSignalRetentionDays = 90,
                    EnableReplayVerification = false
                }
            },
            lessonExtractor: extractor.Object);

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        // SchemaCorrection landed with the deterministic REGEX template — never the extractor's free text.
        var schemaCorrection = captured.Should().ContainSingle(x => x.PatternType == McpPatternType.SchemaCorrection).Subject;
        schemaCorrection.PatternContent.Should().Be(
            "NEVER use 'created_at' on public.orders — correct column is 'created_on'");
        extractor.Verify(
            x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Never);

        // CommonQuery and DocumentationGap are skipped entirely under the lock.
        captured.Should().NotContain(x => x.PatternType == McpPatternType.CommonQuery);
        captured.Should().NotContain(x => x.PatternType == McpPatternType.DocumentationGap);

        // JoinPattern still lands (structural), but with no free-text examples.
        var joinPattern = captured.Should().ContainSingle(x => x.PatternType == McpPatternType.JoinPattern).Subject;
        joinPattern.ExampleQuestion.Should().BeNull();
        joinPattern.ExampleSql.Should().BeNull();
    }

    [Test]
    public async Task Unlocked_StillMinesEveryDetector_AndKeepsJoinExamples()
    {
        // Review F4 (test lane): the locked test proves the detectors are SKIPPED, but nothing proved they still
        // RUN when the lock is off. A regression that skipped them unconditionally — the catastrophic direction,
        // since it silently stops all learning — would have passed the whole suite.
        const int UnlockedProjectId = 5;
        var signals = new List<McpQuerySignal>
        {
            CorrectionSignalFor(UnlockedProjectId, "public.orders", "created_at", "created_on"),
            CorrectionSignalFor(UnlockedProjectId, "public.orders", "created_at", "created_on"),
            CorrectionSignalFor(UnlockedProjectId, "public.orders", "created_at", "created_on"),
            JoinSignal(UnlockedProjectId),
            JoinSignal(UnlockedProjectId),
            GapSignal(UnlockedProjectId, isSuccessful: false),
            GapSignal(UnlockedProjectId, isSuccessful: false),
            GapSignal(UnlockedProjectId, isSuccessful: true),
            GapSignal(UnlockedProjectId, isSuccessful: true),
            GapSignal(UnlockedProjectId, isSuccessful: true)
        };

        var captured = new List<McpLearnedPattern>();
        var service = BuildAggregationService(
            signals,
            captured,
            perProject: new Dictionary<int, McpSettingsData>
            {
                [UnlockedProjectId] = new McpSettingsData
                {
                    EnableLearning = true,
                    RetainQueryContent = true,
                    LearningSignalRetentionDays = 90,
                    EnableReplayVerification = false
                }
            });

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        captured.Should().Contain(x => x.PatternType == McpPatternType.SchemaCorrection);
        captured.Should().Contain(x => x.PatternType == McpPatternType.CommonQuery,
            "the CommonQuery detector must still run when the content lock is off");
        captured.Should().Contain(x => x.PatternType == McpPatternType.DocumentationGap,
            "the DocumentationGap detector must still run when the content lock is off");

        var joinPattern = captured.Should().ContainSingle(x => x.PatternType == McpPatternType.JoinPattern).Subject;
        joinPattern.ExampleQuestion.Should().NotBeNull("examples are retained when the lock is off");
        joinPattern.ExampleSql.Should().NotBeNull();
    }

    [TestCase(0, 90)]
    [TestCase(0, 0)]
    public async Task AggregateLearnedPatternsAsync_StoredRetentionWindowBelowOneDay_StillMinesRecentSignals(int projectDays, int globalDays)
    {
        // A window stored before the settings handlers refused one below a day falls back like the cleanup does, instead
        // of leaving an empty mining window.
        var signals = new List<McpQuerySignal> { CorrectionSignal(), CorrectionSignal(), CorrectionSignal() };
        var captured = new List<McpLearnedPattern>();
        var service = BuildAggregationService(
            signals,
            captured,
            perProject: new Dictionary<int, McpSettingsData>
            {
                [ProjectId] = new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = projectDays, EnableReplayVerification = false }
            },
            globalSettings: new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = globalDays, EnableReplayVerification = false });

        await service.AggregateLearnedPatternsAsync(CancellationToken.None);

        captured.Should().Contain(x => x.PatternType == McpPatternType.SchemaCorrection && x.ColumnName == "created_at");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_UsesEachProjectsRetentionWindow_AndTheGlobalWindowForProjectlessSignals()
    {
        // T-F003: retention is per project. Project 1 keeps 10 days, project 2 keeps 100, projectless signals
        // keep the global 90. Only the rows older than THEIR window are deleted.
        var p1Old = AgedSignal(projectId: 1, ageDays: 15);
        var p1Fresh = AgedSignal(projectId: 1, ageDays: 5);
        var p2Old = AgedSignal(projectId: 2, ageDays: 95);
        var noneOld = AgedSignal(projectId: null, ageDays: 95);
        var noneFresh = AgedSignal(projectId: null, ageDays: 30);
        var signals = new List<McpQuerySignal> { p1Old, p1Fresh, p2Old, noneOld, noneFresh };
        var service = BuildAggregationService(signals, [], perProject: new Dictionary<int, McpSettingsData>
        {
            [1] = new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = 10 },
            [2] = new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = 100 }
        });

        await service.CleanupOldSignalsAsync(ct: CancellationToken.None);

        signals.Should().Equal([p1Fresh, p2Old, noneFresh],
            "p1's 15-day row exceeds its 10-day window; p2's 95-day row is kept ONLY because of its 100-day override (the global 90 would drop it); the 95-day projectless row exceeds the global 90");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_ThroughTheInterface_GlobalLearningOff_StillAppliesEachProjectsWindow()
    {
        // Learning is off globally but on for project 1 (30-day window), so its signals are still recorded and must
        // still expire. Called the way the job calls it: through the interface, with no argument.
        var p1Old = AgedSignal(projectId: 1, ageDays: 45);
        var p1Fresh = AgedSignal(projectId: 1, ageDays: 10);
        var noneOld = AgedSignal(projectId: null, ageDays: 95);
        var noneFresh = AgedSignal(projectId: null, ageDays: 30);
        var signals = new List<McpQuerySignal> { p1Old, p1Fresh, noneOld, noneFresh };
        IMcpLearningAggregationService service = BuildAggregationService(
            signals,
            [],
            perProject: new Dictionary<int, McpSettingsData>
            {
                [1] = new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = 30 }
            },
            globalSettings: new McpSettingsData { EnableLearning = false, LearningSignalRetentionDays = 90 });

        await service.CleanupOldSignalsAsync();

        signals.Should().Equal([p1Fresh, noneFresh],
            "project 1's 45-day row exceeds its 30-day window and the 95-day projectless row exceeds the global 90, whatever the global learning switch says");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_LegacyWindowsBelowOneDay_FallBackToTheDefault_NeverDeleteRecentSignals()
    {
        // Rows stored before the settings handlers refused a window below one day: the global 0 falls back to the
        // default 90 days, project 1's 0 to that global window, project 2's 10 days still applies.
        var p1Recent = AgedSignal(projectId: 1, ageDays: 1);
        var p1Old = AgedSignal(projectId: 1, ageDays: 95);
        var p2Old = AgedSignal(projectId: 2, ageDays: 15);
        var noneRecent = AgedSignal(projectId: null, ageDays: 1);
        var noneOld = AgedSignal(projectId: null, ageDays: 95);
        var signals = new List<McpQuerySignal> { p1Recent, p1Old, p2Old, noneRecent, noneOld };
        var service = BuildAggregationService(
            signals,
            [],
            perProject: new Dictionary<int, McpSettingsData>
            {
                [1] = new McpSettingsData { LearningSignalRetentionDays = 0 },
                [2] = new McpSettingsData { LearningSignalRetentionDays = 10 }
            },
            globalSettings: new McpSettingsData { LearningSignalRetentionDays = 0 });

        await service.CleanupOldSignalsAsync(ct: CancellationToken.None);

        signals.Should().Equal([p1Recent, noneRecent],
            "a window below one day is the default 90 days, never \"delete every signal\"; project 2's own 10 days still applies");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_AProjectsLegacyWindowBelowOneDay_UsesTheGlobalWindow()
    {
        var p1Recent = AgedSignal(projectId: 1, ageDays: 10);
        var p1Old = AgedSignal(projectId: 1, ageDays: 45);
        var signals = new List<McpQuerySignal> { p1Recent, p1Old };
        var service = BuildAggregationService(
            signals,
            [],
            perProject: new Dictionary<int, McpSettingsData> { [1] = new McpSettingsData { LearningSignalRetentionDays = -1 } },
            globalSettings: new McpSettingsData { LearningSignalRetentionDays = 30 });

        await service.CleanupOldSignalsAsync(ct: CancellationToken.None);

        signals.Should().Equal([p1Recent], "project 1's -1 is the global 30 days");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_OneProjectFailing_StillPurgesTheOthers_ThenThrows()
    {
        // Project 2 comes first and fails; project 1 and the projectless signals are still cleaned up.
        var p2Old = AgedSignal(projectId: 2, ageDays: 95);
        var p1Old = AgedSignal(projectId: 1, ageDays: 95);
        var p1Fresh = AgedSignal(projectId: 1, ageDays: 5);
        var noneOld = AgedSignal(projectId: null, ageDays: 95);
        var signals = new List<McpQuerySignal> { p2Old, p1Old, p1Fresh, noneOld };
        var service = BuildAggregationService(
            signals,
            [],
            configureSettings: x => x
                .Setup(y => y.GetEffectiveSettingsAsync(2, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("settings row unreadable")));

        var act = () => service.CleanupOldSignalsAsync(ct: CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*1 project(s) could not be cleaned up*");
        signals.Should().Equal([p2Old, p1Fresh], "project 2 was skipped; project 1 and the projectless signals were cleaned up");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_Cancelled_Propagates_WithoutTheRemainingDeletes()
    {
        var p1Old = AgedSignal(projectId: 1, ageDays: 95);
        var noneOld = AgedSignal(projectId: null, ageDays: 95);
        var signals = new List<McpQuerySignal> { p1Old, noneOld };
        var service = BuildAggregationService(
            signals,
            [],
            configureSettings: x => x
                .Setup(y => y.GetEffectiveSettingsAsync(1, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new OperationCanceledException()));

        var act = () => service.CleanupOldSignalsAsync(ct: CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        signals.Should().Equal([p1Old, noneOld], "a cancelled run stops at once");
    }

    [Test]
    public async Task CleanupOldSignalsAsync_ExplicitRetentionDays_WinsOverEveryWindow()
    {
        var p1 = AgedSignal(projectId: 1, ageDays: 15);
        var p2 = AgedSignal(projectId: 2, ageDays: 15);
        var none = AgedSignal(projectId: null, ageDays: 15);
        var fresh = AgedSignal(projectId: 1, ageDays: 1);
        var signals = new List<McpQuerySignal> { p1, p2, none, fresh };
        var service = BuildAggregationService(signals, [], perProject: new Dictionary<int, McpSettingsData>
        {
            [1] = new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = 100 },
            [2] = new McpSettingsData { EnableLearning = true, LearningSignalRetentionDays = 100 }
        });

        await service.CleanupOldSignalsAsync(retentionDays: 7, ct: CancellationToken.None);

        signals.Should().Equal([fresh], "an explicit retentionDays argument overrides both project and global windows");
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ColumnWithAnExtractedLesson_RefreshesItWithoutCallingTheExtractor_WhileItsSignalCountIsUnchanged()
    {
        var extractor = ExtractorReturningALesson();
        var stored = new McpLearnedPattern
        {
            Id = 41,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            ColumnName = "created_at",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = "A lesson the extractor wrote earlier",
            SignalCount = 3,
            Confidence = 0.5,
            Status = McpPatternStatus.NeedsEvidence
        };
        var saved = new List<object>();
        var (service, context) = BuildRecordingService(extractor.Object, [stored], saved);
        var extraction = new McpLearningAggregationService.ExtractionStats();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, [CorrectionSignal(), CorrectionSignal(), CorrectionSignal()], extraction, retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Never);
        stored.PatternContent.Should().Be("A lesson the extractor wrote earlier");
        stored.Confidence.Should().BeApproximately(0.95, 0.0001);
        saved.Should().BeEmpty();
        extraction.Attempts.Should().Be(0);
        extraction.ReusedExtractions.Should().Be(1);
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ColumnWithAnExtractedLesson_AsksTheExtractorAgainWhenSignalsWereAdded()
    {
        var extractor = ExtractorReturningALesson();
        var stored = new McpLearnedPattern
        {
            Id = 43,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            ColumnName = "created_at",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = "A lesson the extractor wrote earlier",
            SignalCount = 2,
            Status = McpPatternStatus.NeedsEvidence
        };
        var (service, context) = BuildRecordingService(extractor.Object, [stored], []);

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, [CorrectionSignal(), CorrectionSignal(), CorrectionSignal()], new McpLearningAggregationService.ExtractionStats(), retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ColumnWithOnlyTheDeterministicContent_StillAsksTheExtractor()
    {
        var extractor = ExtractorReturningALesson();
        var stored = new McpLearnedPattern
        {
            Id = 42,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            ColumnName = "created_at",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = "NEVER use 'created_at' on public.orders — correct column is 'created_on'",
            Status = McpPatternStatus.NeedsEvidence
        };
        var (service, context) = BuildRecordingService(extractor.Object, [stored], []);
        var extraction = new McpLearningAggregationService.ExtractionStats();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, [CorrectionSignal(), CorrectionSignal(), CorrectionSignal()], extraction, retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Once);
        extraction.ReusedExtractions.Should().Be(0);
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_MoreClustersThanTheCap_ExtractsExactlyTheCapAndTheRestTakeTheDeterministicContent()
    {
        var extractor = ExtractorReturningALesson();
        var saved = new List<object>();
        var (service, context) = BuildRecordingService(extractor.Object, [], saved);
        var extraction = new McpLearningAggregationService.ExtractionStats();
        var clusters = McpLearningAggregationService.MaxLessonExtractionsPerRun + 5;
        var signals = Enumerable.Range(0, clusters)
            .Select(i => CorrectionSignalFor(ProjectId, "public.orders", $"bad_{i:D2}", $"good_{i:D2}"))
            .ToList();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, signals, extraction, retainContent: true, CancellationToken.None);

        extractor.Verify(
            x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Exactly(McpLearningAggregationService.MaxLessonExtractionsPerRun));
        var patterns = saved.OfType<McpLearnedPattern>().ToList();
        patterns.Should().HaveCount(clusters);
        patterns.Count(x => x.PatternContent == "An extracted lesson").Should().Be(McpLearningAggregationService.MaxLessonExtractionsPerRun);
        patterns.Count(x => x.PatternContent.StartsWith("NEVER use 'bad_")).Should().Be(5);
        extraction.Attempts.Should().Be(McpLearningAggregationService.MaxLessonExtractionsPerRun);
        extraction.NullResults.Should().Be(0);
        extraction.CappedExtractions.Should().Be(5);
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_CappedClusters_DoNotCountTowardTheNullResultWarning()
    {
        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        extractor
            .Setup(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExtractedLesson?)null);
        var (service, context) = BuildRecordingService(extractor.Object, [], []);
        var extraction = new McpLearningAggregationService.ExtractionStats();
        var clusters = McpLearningAggregationService.MaxLessonExtractionsPerRun + 3;
        var signals = Enumerable.Range(0, clusters)
            .Select(i => CorrectionSignalFor(ProjectId, "public.orders", $"bad_{i:D2}", $"good_{i:D2}"))
            .ToList();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, signals, extraction, retainContent: true, CancellationToken.None);

        extraction.Attempts.Should().Be(McpLearningAggregationService.MaxLessonExtractionsPerRun);
        extraction.NullResults.Should().Be(extraction.Attempts, "every attempted extraction was null, so the run warns about the provider");
        extraction.CappedExtractions.Should().Be(3);
    }

    // An LLM lesson can have the same shape as the template ("NEVER use '...'"): only the exact template text is the fallback's.
    [TestCase("NEVER use 'created_at' on public.orders — prefer created_on in every query", 0)]
    [TestCase("Column 'created_at' does not exist on public.orders — check schema, it was renamed", 0)]
    [TestCase("Column 'created_at' does not exist on public.orders — check schema", 1)]
    public async Task DetectSchemaCorrectionsAsync_OnlyTheExactTemplateTextIsNotAnExtractedLesson(string storedContent, int extractorCalls)
    {
        var extractor = ExtractorReturningALesson();
        var stored = StoredLesson(44, "created_at", storedContent, signalCount: 3, McpPatternStatus.NeedsEvidence);
        var (service, context) = BuildRecordingService(extractor.Object, [stored], []);

        // No correct column can be found for these signals, so the template is the "Column '…' does not exist" text.
        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, [NoReplacementSignal(), NoReplacementSignal(), NoReplacementSignal()], new McpLearningAggregationService.ExtractionStats(), retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Exactly(extractorCalls));
    }

    [TestCase(McpPatternStatus.NeedsEvidence)]
    [TestCase(McpPatternStatus.Approved)]
    [TestCase(McpPatternStatus.AutoApproved)]
    [TestCase(McpPatternStatus.Rejected)]
    public async Task DetectSchemaCorrectionsAsync_AReusedLesson_RefreshesItsTimestampWhateverItsStatus(McpPatternStatus status)
    {
        var extractor = ExtractorReturningALesson();
        var stored = StoredLesson(45, "created_at", "A lesson the extractor wrote earlier", signalCount: 1, status);
        stored.LastRefreshedAt = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var (service, context) = BuildRecordingService(extractor.Object, [stored], []);

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, [CorrectionSignal()], new McpLearningAggregationService.ExtractionStats(), retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Never);
        stored.Status.Should().Be(status);
        stored.PatternContent.Should().Be("A lesson the extractor wrote earlier");
        stored.LastRefreshedAt.Should().BeAfter(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ClustersPastTheCap_KeepTheirStoredLesson_AndTheCapGoesToClustersWithoutOne()
    {
        var extractor = ExtractorReturningALesson();
        var cap = McpLearningAggregationService.MaxLessonExtractionsPerRun;
        var withLesson = Enumerable.Range(0, cap + 2)
            .Select(i => StoredLesson(100 + i, $"bad_{i:D2}", $"stored lesson {i}", signalCount: 5, McpPatternStatus.AutoApproved))
            .ToList();
        var saved = new List<object>();
        var (service, context) = BuildRecordingService(extractor.Object, withLesson, saved);
        var extraction = new McpLearningAggregationService.ExtractionStats();
        // Three more clusters have no stored lesson; they come last in the signals but first in line for the cap.
        var signals = Enumerable.Range(0, cap + 5)
            .Select(i => CorrectionSignalFor(ProjectId, "public.orders", $"bad_{i:D2}", $"good_{i:D2}"))
            .ToList();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, signals, extraction, retainContent: true, CancellationToken.None);

        extractor.Verify(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()), Times.Exactly(cap));
        saved.OfType<McpLearnedPattern>().Should().HaveCount(3, "only the clusters without a stored lesson add rows");
        saved.OfType<McpLearnedPattern>().Should().OnlyContain(x => x.PatternContent == "An extracted lesson");
        var kept = withLesson.Where(x => x.PatternContent.StartsWith("stored lesson")).ToList();
        kept.Should().HaveCount(5, "22 clusters with a lesson, 17 of them in the 20 - 3 calls left after the lesson-less ones");
        kept.Should().OnlyContain(x => x.Status == McpPatternStatus.AutoApproved && x.SignalCount == 1);
        extraction.CappedExtractions.Should().Be(5);
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ANullResult_KeepsTheStoredLesson_AndRefreshesItsStatistics()
    {
        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        extractor
            .Setup(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ExtractedLesson?)null);
        var stored = StoredLesson(46, "created_at", "A lesson the extractor wrote earlier", signalCount: 5, McpPatternStatus.AutoApproved);
        var saved = new List<object>();
        var (service, context) = BuildRecordingService(extractor.Object, [stored], saved);
        var extraction = new McpLearningAggregationService.ExtractionStats();

        await service.DetectSchemaCorrectionsAsync(
            context, ProjectId, DataSourceId, [CorrectionSignal()], extraction, retainContent: true, CancellationToken.None);

        stored.PatternContent.Should().Be("A lesson the extractor wrote earlier");
        stored.Status.Should().Be(McpPatternStatus.AutoApproved);
        stored.SignalCount.Should().Be(1);
        saved.Should().BeEmpty();
        extraction.Attempts.Should().Be(1);
        extraction.NullResults.Should().Be(1);
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_TheCapIsSharedByTheDataSourcesOfAProject_AndEachProjectHasItsOwn()
    {
        var extractor = ExtractorReturningALesson();
        var cap = McpLearningAggregationService.MaxLessonExtractionsPerRun;
        var (service, context) = BuildRecordingService(extractor.Object, [], []);
        var extraction = new McpLearningAggregationService.ExtractionStats();
        List<McpQuerySignal> Clusters(int count) => Enumerable.Range(0, count)
            .Select(i => CorrectionSignalFor(ProjectId, "public.orders", $"bad_{i:D2}", $"good_{i:D2}"))
            .ToList();

        await service.DetectSchemaCorrectionsAsync(context, ProjectId, DataSourceId, Clusters(cap - 8), extraction, retainContent: true, CancellationToken.None);
        await service.DetectSchemaCorrectionsAsync(context, ProjectId, DataSourceId + 1, Clusters(cap - 8), extraction, retainContent: true, CancellationToken.None);

        extraction.Attempts.Should().Be(cap, "two data sources of one project draw on one cap");
        extraction.CappedExtractions.Should().Be((cap - 8) * 2 - cap);

        await service.DetectSchemaCorrectionsAsync(context, ProjectId + 1, DataSourceId, Clusters(cap), extraction, retainContent: true, CancellationToken.None);

        extraction.Attempts.Should().Be(cap * 2, "another project has its own cap");
    }

    [Test]
    public async Task DetectSchemaCorrectionsAsync_ReusedLessons_DoNotConsumeTheCap()
    {
        var extractor = ExtractorReturningALesson();
        var cap = McpLearningAggregationService.MaxLessonExtractionsPerRun;
        var withLesson = Enumerable.Range(0, cap)
            .Select(i => StoredLesson(200 + i, $"old_{i:D2}", $"stored lesson {i}", signalCount: 1, McpPatternStatus.NeedsEvidence))
            .ToList();
        var (service, context) = BuildRecordingService(extractor.Object, withLesson, []);
        var extraction = new McpLearningAggregationService.ExtractionStats();
        var signals = Enumerable.Range(0, cap)
            .Select(i => CorrectionSignalFor(ProjectId, "public.orders", $"old_{i:D2}", $"good_{i:D2}"))
            .Concat(Enumerable.Range(0, cap).Select(i => CorrectionSignalFor(ProjectId, "public.orders", $"new_{i:D2}", $"good_{i:D2}")))
            .ToList();

        await service.DetectSchemaCorrectionsAsync(context, ProjectId, DataSourceId, signals, extraction, retainContent: true, CancellationToken.None);

        extraction.ReusedExtractions.Should().Be(cap);
        extraction.Attempts.Should().Be(cap);
        extraction.CappedExtractions.Should().Be(0);
    }

    private static McpLearnedPattern StoredLesson(int id, string column, string content, int signalCount, McpPatternStatus status) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "orders",
            ColumnName = column,
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = content,
            SignalCount = signalCount,
            Confidence = 0.5,
            Status = status
        };

    private static McpQuerySignal NoReplacementSignal()
    {
        var signal = CorrectionSignalFor(ProjectId, "public.orders", "created_at", "created_on");
        signal.SchemaValidationError = "Column 'created_at' does not exist on 'l'. Available: id";
        signal.CorrectedSql = "SELECT id FROM public.orders";

        return signal;
    }

    private static Mock<ILessonExtractor> ExtractorReturningALesson()
    {
        var extractor = new Mock<ILessonExtractor>();
        extractor.SetupGet(x => x.IsAvailable).Returns(true);
        extractor
            .Setup(x => x.ExtractAsync(It.IsAny<FailureCluster>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ExtractedLesson(McpPatternType.SchemaCorrection, "An extracted lesson", null, null, "symptom", "root cause", "rule", null, null));

        return extractor;
    }

    private static (McpLearningAggregationService Service, BeaconContext Context) BuildRecordingService(
        ILessonExtractor extractor, List<McpLearnedPattern> existing, List<object> saved)
    {
        var context = new RecordingBeaconContext(
            new Dictionary<Type, object> { [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(existing, saved) },
            saved);
        var service = new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: extractor,
            replayVerifier: null);

        return (service, context);
    }

    private static McpQuerySignal AgedSignal(int? projectId, int ageDays)
    {
        return new McpQuerySignal
        {
            Tool = "ask",
            Question = "q",
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            CreatedTime = DateTime.UtcNow.AddDays(-ageDays),
            IsSuccessful = true
        };
    }

    private static McpQuerySignal CorrectionSignalFor(int projectId, string table, string badColumn, string goodColumn)
    {
        var shortTable = table.Contains('.') ? table[(table.IndexOf('.') + 1)..] : table;

        return new McpQuerySignal
        {
            Tool = "ask",
            Question = $"{shortTable} created last month",
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            SchemaValidationFailed = true,
            SchemaValidationError = $"Column '{badColumn}' does not exist on 'l'. Available: {goodColumn}, id",
            RetryAttempted = true,
            RetrySucceeded = true,
            GeneratedSql = $"SELECT id FROM {table} WHERE {badColumn} > 0",
            CorrectedSql = $"SELECT id FROM {table} WHERE {goodColumn} > 0",
            TablesUsed = $"[\"{table}\"]",
            IsSuccessful = true
        };
    }

    private static McpQuerySignal CorrectionSignal()
    {
        return new McpQuerySignal
        {
            Tool = "ask",
            Question = "orders created last month",
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaValidationFailed = true,
            SchemaValidationError = "Column 'created_at' does not exist on 'l'. Available: created_on, id",
            RetryAttempted = true,
            RetrySucceeded = true,
            GeneratedSql = "SELECT id FROM public.orders WHERE created_at > now()",
            CorrectedSql = "SELECT id FROM public.orders WHERE created_on > now()",
            TablesUsed = "[\"public.orders\"]",
            IsSuccessful = true
        };
    }

    private static McpQuerySignal JoinSignal(int projectId)
    {
        return new McpQuerySignal
        {
            Tool = "ask",
            Question = "orders with their customer",
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            GeneratedSql = "SELECT o.id FROM public.orders o JOIN public.customers c ON c.id = o.customer_id",
            TablesUsed = "[\"public.orders\", \"public.customers\"]",
            IsSuccessful = true
        };
    }

    private static McpQuerySignal GapSignal(int projectId, bool isSuccessful)
    {
        return new McpQuerySignal
        {
            Tool = "ask",
            Question = "gap table question",
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            TablesUsed = "[\"public.gaps\"]",
            IsSuccessful = isSuccessful
        };
    }

    // A dry_run signal shaped EXACTLY like a mineable correction cluster member — if the taxonomy
    // filter ever leaks it into the detectors, a public.tickets/status_x pattern appears.
    private static McpQuerySignal DryRunCorrectionSignal()
    {
        return new McpQuerySignal
        {
            Tool = "dry_run",
            Question = "SELECT id FROM public.tickets WHERE status_x = 'open'",
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            SchemaValidationFailed = true,
            SchemaValidationError = "Column 'status_x' does not exist on 't'. Available: status, id",
            RetryAttempted = true,
            RetrySucceeded = true,
            GeneratedSql = "SELECT id FROM public.tickets WHERE status_x = 'open'",
            CorrectedSql = "SELECT id FROM public.tickets WHERE status = 'open'",
            TablesUsed = "[\"public.tickets\"]",
            IsSuccessful = false
        };
    }

    private static McpLearningAggregationService BuildAggregationService(
        List<McpQuerySignal> signals,
        List<McpLearnedPattern> captured,
        IReadOnlyDictionary<int, McpSettingsData>? perProject = null,
        ILessonExtractor? lessonExtractor = null,
        McpSettingsData? globalSettings = null,
        Action<Mock<IMcpSettingsProvider>>? configureSettings = null)
    {
        var patternSet = BuildDbSet(Array.Empty<McpLearnedPattern>());
        patternSet
            .Setup(x => x.Add(It.IsAny<McpLearnedPattern>()))
            .Callback<McpLearnedPattern>(captured.Add);

        // ExecuteDeleteAsync over the double removes the matched rows from the backing list (CleanupOldSignalsAsync).
        var signalSet = BuildDbSet(signals, matched =>
        {
            foreach (var row in matched)
            {
                signals.Remove(row);
            }

            return matched.Count;
        });
        var context = new DetectionTestContext(patternSet.Object, signalSet.Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settingsProvider = SettingsProviderMock.Create(
            globalSettings ?? new McpSettingsData
            {
                EnableLearning = true,
                LearningSignalRetentionDays = 90,
                EnableReplayVerification = false
            },
            projectSettings: perProject);
        configureSettings?.Invoke(settingsProvider);

        return new McpLearningAggregationService(
            factory.Object,
            settingsProvider.Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: lessonExtractor,
            replayVerifier: null);
    }

    private static (McpLearningAggregationService Service, BeaconContext Context) BuildService(
        ILessonExtractor extractor, List<McpLearnedPattern> captured)
    {
        var patternSet = BuildDbSet(Array.Empty<McpLearnedPattern>());
        patternSet
            .Setup(x => x.Add(It.IsAny<McpLearnedPattern>()))
            .Callback<McpLearnedPattern>(captured.Add);

        var context = new DetectionTestContext(patternSet.Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var service = new McpLearningAggregationService(
            factory.Object,
            SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: extractor,
            replayVerifier: null);

        return (service, context);
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data, Func<IReadOnlyList<T>, int>? onExecuteDelete = null) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider, onExecuteDelete));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(queryable.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(queryable.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        return set;
    }

    /// <summary>
    /// A <see cref="BeaconContext"/> whose McpLearnedPatterns set resolves to the supplied mocked set.
    /// SaveChanges is a no-op; new candidates are observed through the mocked set's Add capture.
    /// </summary>
    private sealed class DetectionTestContext : BeaconContext
    {
        private static readonly DbContextOptions<DetectionTestContext> Options =
            new DbContextOptionsBuilder<DetectionTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpLearnedPattern> _patterns;
        private readonly DbSet<McpQuerySignal>? _signals;

        public DetectionTestContext(DbSet<McpLearnedPattern> patterns, DbSet<McpQuerySignal>? signals = null)
            : base(Options, "beacon")
        {
            _patterns = patterns;
            _signals = signals;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpLearnedPattern))
            {
                return (DbSet<TEntity>)(object)_patterns;
            }

            if (typeof(TEntity) == typeof(McpQuerySignal) && _signals != null)
            {
                return (DbSet<TEntity>)(object)_signals;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
