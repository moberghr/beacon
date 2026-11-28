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
/// Promotion is now REPLAY-GATED (§ Architecture ⑥), not confidence-gated. New candidates are created
/// NeedsEvidence; the aggregation job's <see cref="McpLearningAggregationService.PromoteVerifiedCandidatesAsync"/>
/// promotes a candidate to AutoApproved ONLY when the replay verifier's verdict passes. When the verdict
/// fails, replay is disabled, or no verifier is wired, the candidate stays NeedsEvidence (safe default —
/// never auto-approve without measured evidence). These tests exercise the promotion step in isolation over
/// a mocked <see cref="BeaconContext"/> (async-queryable doubles, no DB — §4.7).
/// </summary>
[TestFixture]
public class ReplayGatedPromotionTests
{
    private const int ProjectId = 1;

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_VerdictPasses_PromotesToAutoApproved()
    {
        var candidate = NeedsEvidencePattern();
        var verifier = VerifierReturning(passed: true);
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.AutoApproved);
        candidate.LastVerifiedAt.Should().NotBeNull();
        candidate.LastReplayPassed.Should().BeTrue();
        verifier.Verify(
            x => x.VerifyAsync(candidate, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_VerdictFails_StaysNeedsEvidence_AndStoresTheFailedReplay()
    {
        var candidate = NeedsEvidencePattern();
        var verifier = VerifierReturning(passed: false);
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);
        var before = DateTime.UtcNow;

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.LastReplayPassed.Should().BeFalse();
        candidate.LastVerifiedAt.Should().NotBeNull().And.BeOnOrAfter(before);
        candidate.ContentHash.Should().Be(
            GroundingContentHash.OfPattern(candidate.PatternContent, candidate.ExampleQuestion, candidate.ExampleSql),
            "a pattern stored without a digest gets the measured one first, and the verdict is written against it");
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_NoRelevantGoldenCase_StoresNoVerdict()
    {
        // Nothing was measured: the verdict says nothing about the candidate, so it is not stored as a failed replay.
        var candidate = NeedsEvidencePattern();
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 0, BaselineFailing: 0, Flipped: 0, Regressions: 0, Errored: 0, Passed: false));
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.LastVerifiedAt.Should().BeNull();
        candidate.LastReplayPassed.Should().BeNull();
        verifier.Verify(x => x.VerifyAsync(candidate, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(0.9, 0.95, false)]
    [TestCase(0.9, 0.9, true)]
    [TestCase(0.9, 0.0, true)]
    public async Task PromoteVerifiedCandidatesAsync_ACandidateBelowTheThreshold_IsNotReplayed_AndStaysNeedsEvidence(
        double confidence, double threshold, bool replayed)
    {
        var candidate = NeedsEvidencePattern();
        candidate.Confidence = confidence;
        var verifier = VerifierReturning(passed: true);
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);
        var settings = EnabledSettings();
        settings.LearningAutoApproveThreshold = threshold;

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, settings, null, CancellationToken.None);

        verifier.Verify(
            x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            replayed ? Times.Once() : Times.Never());
        candidate.Status.Should().Be(replayed ? McpPatternStatus.AutoApproved : McpPatternStatus.NeedsEvidence);
        candidate.LastReplayPassed.Should().Be(replayed ? true : null);
        (candidate.LastVerifiedAt != null).Should().Be(replayed);
    }

    [TestCase(McpPatternStatus.Rejected)]
    [TestCase(McpPatternStatus.Approved)]
    public async Task PromoteVerifiedCandidatesAsync_ADecisionDuringTheReplay_KeepsTheFailedVerdictOffTheRow(McpPatternStatus decided)
    {
        // A person decides while the replay runs: the failed verdict is written only while the row is still NeedsEvidence.
        var candidate = NeedsEvidencePattern();
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => candidate.Status = decided)
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 2, BaselineFailing: 1, Flipped: 0, Regressions: 1, Errored: 0, Passed: false));
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(decided);
        candidate.LastVerifiedAt.Should().BeNull();
        candidate.LastReplayPassed.Should().BeNull();
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_ARefreshDuringTheReplay_KeepsTheFailedVerdictOffTheNewContent()
    {
        // The detection pass refreshes the content while the replay runs: the verdict was measured on the old content.
        var candidate = NeedsEvidencePattern();
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Callback(() => candidate.PatternContent = "NEVER use 'created_at' on public.loans — correct column is 'opened_time'")
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 1, BaselineFailing: 1, Flipped: 0, Regressions: 0, Errored: 0, Passed: false));
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.LastVerifiedAt.Should().BeNull();
        candidate.LastReplayPassed.Should().BeNull();
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_AllRelevantCasesErrored_StaysNeedsEvidence()
    {
        // Errored == RelevantCases means replay could not measure the candidate at all (e.g. a data-source
        // outage). The verdict does NOT pass, so the candidate must remain NeedsEvidence — never promoted on
        // an unmeasured run. Now that Errored is a distinct field, assert this explicitly.
        var candidate = NeedsEvidencePattern();
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 3, BaselineFailing: 0, Flipped: 0, Regressions: 0, Errored: 3, Passed: false));
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.LastVerifiedAt.Should().BeNull();
        candidate.LastReplayPassed.Should().BeNull("an unmeasured replay is no failed verdict");
        verifier.Verify(
            x => x.VerifyAsync(candidate, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_FailedReplayWithSomeCasesErrored_StoresNoVerdict()
    {
        // Two of three relevant cases ran and the replay failed, but the case that errored might have passed: that is no
        // failed verdict. Nothing is stored (a stored false would refuse the pattern's documentation patches) and the
        // candidate stays NeedsEvidence for the next run.
        var candidate = NeedsEvidencePattern();
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 3, BaselineFailing: 2, Flipped: 0, Regressions: 0, Errored: 1, Passed: false));
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        candidate.LastReplayPassed.Should().BeNull("a failed replay with errored cases is no failed verdict");
        candidate.LastVerifiedAt.Should().BeNull();
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_PassedReplayWithSomeCasesErrored_StoresThePassAndPromotes()
    {
        // A pass keeps its rule: the verifier passed it on the cases that ran, so the verdict is stored and the candidate
        // is auto-approved.
        var candidate = NeedsEvidencePattern();
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 3, BaselineFailing: 2, Flipped: 1, Regressions: 0, Errored: 1, Passed: true));
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.AutoApproved);
        candidate.LastReplayPassed.Should().BeTrue();
        candidate.LastVerifiedAt.Should().NotBeNull();
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_NoVerifierWired_StaysNeedsEvidence()
    {
        var candidate = NeedsEvidencePattern();
        var (service, context) = BuildService(new[] { candidate }, replayVerifier: null);

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_ReplayDisabled_NeverCallsVerifier_StaysNeedsEvidence()
    {
        var candidate = NeedsEvidencePattern();
        var verifier = VerifierReturning(passed: true);
        var (service, context) = BuildService(new[] { candidate }, verifier.Object);

        var disabled = EnabledSettings();
        disabled.EnableReplayVerification = false;

        await service.PromoteVerifiedCandidatesAsync(context, ProjectId, disabled, null, CancellationToken.None);

        candidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        verifier.Verify(
            x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task PromoteVerifiedCandidatesAsync_OneCandidateThrows_IsIsolated_OthersStillPromoted()
    {
        // Per-candidate try/catch isolation (§ silent-failure F3): a verifier that THROWS for one candidate
        // must not abort the whole promotion loop — the remaining candidates are still measured and promoted,
        // and the method itself completes without throwing.
        var throwingCandidate = NeedsEvidencePattern(id: 11);
        var passingCandidate = NeedsEvidencePattern(id: 22);

        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(throwingCandidate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("replay boom"));
        verifier
            .Setup(x => x.VerifyAsync(passingCandidate, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 1, BaselineFailing: 1, Flipped: 1, Regressions: 0, Errored: 0, Passed: true));

        // Throwing candidate FIRST so a loop without per-candidate isolation would never reach the second.
        var (service, context) = BuildService(new[] { throwingCandidate, passingCandidate }, verifier.Object);

        var act = async () =>
            await service.PromoteVerifiedCandidatesAsync(context, ProjectId, EnabledSettings(), null, CancellationToken.None);

        await act.Should().NotThrowAsync();

        // The passing candidate was promoted despite the earlier throw.
        passingCandidate.Status.Should().Be(McpPatternStatus.AutoApproved);
        passingCandidate.LastVerifiedAt.Should().NotBeNull();

        // The throwing candidate was swallowed-and-left, staying NeedsEvidence with no verified timestamp.
        throwingCandidate.Status.Should().Be(McpPatternStatus.NeedsEvidence);
        throwingCandidate.LastVerifiedAt.Should().BeNull();

        verifier.Verify(
            x => x.VerifyAsync(throwingCandidate, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
        verifier.Verify(
            x => x.VerifyAsync(passingCandidate, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static McpLearnedPattern NeedsEvidencePattern()
    {
        return new McpLearnedPattern
        {
            Id = 7,
            ProjectId = ProjectId,
            DataSourceId = 1,
            SchemaName = "public",
            TableName = "loans",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = "NEVER use 'created_at' on public.loans — correct column is 'created_time'",
            Confidence = 0.9,
            Status = McpPatternStatus.NeedsEvidence
        };
    }

    private static McpLearnedPattern NeedsEvidencePattern(int id)
    {
        var pattern = NeedsEvidencePattern();
        pattern.Id = id;
        return pattern;
    }

    private static Mock<IPatternReplayVerifier> VerifierReturning(bool passed)
    {
        var verifier = new Mock<IPatternReplayVerifier>();
        verifier
            .Setup(x => x.VerifyAsync(It.IsAny<McpLearnedPattern>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReplayVerdict(RelevantCases: 1, BaselineFailing: 1, Flipped: 1, Regressions: 0, Errored: 0, Passed: passed));
        return verifier;
    }

    private static McpSettingsData EnabledSettings()
    {
        return new McpSettingsData
        {
            EnableLearning = true,
            EnableReplayVerification = true,
            LearningReplayMinFlips = 1
        };
    }

    private static (McpLearningAggregationService Service, BeaconContext Context) BuildService(
        McpLearnedPattern[] patterns, IPatternReplayVerifier? replayVerifier)
    {
        var context = new PromotionTestContext(BuildDbSet(patterns).Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settingsProvider = SettingsProviderMock.Create();

        var service = new McpLearningAggregationService(
            factory.Object,
            settingsProvider.Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: null,
            replayVerifier: replayVerifier);

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
    /// SaveChanges is a no-op; status mutations are asserted on the shared entity instances.
    /// </summary>
    private sealed class PromotionTestContext : BeaconContext
    {
        private static readonly DbContextOptions<PromotionTestContext> Options =
            new DbContextOptionsBuilder<PromotionTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpLearnedPattern> _patterns;

        public PromotionTestContext(DbSet<McpLearnedPattern> patterns)
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
