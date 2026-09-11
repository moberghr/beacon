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
        ILessonExtractor? lessonExtractor = null)
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
            new McpSettingsData
            {
                EnableLearning = true,
                LearningSignalRetentionDays = 90,
                EnableReplayVerification = false
            },
            projectSettings: perProject);

        return new McpLearningAggregationService(
            factory.Object,
            settingsProvider.Object,
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
