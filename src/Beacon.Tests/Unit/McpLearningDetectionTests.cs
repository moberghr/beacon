using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
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
            context, ProjectId, DataSourceId, signals, extraction, CancellationToken.None);

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
            Mock.Of<IMcpSettingsProvider>(),
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: extractor,
            replayVerifier: null);

        return (service, context);
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(IEnumerable<T> data) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
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

        public DetectionTestContext(DbSet<McpLearnedPattern> patterns)
            : base(Options, "beacon")
        {
            _patterns = patterns;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpLearnedPattern))
            {
                return (DbSet<TEntity>)(object)_patterns;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
