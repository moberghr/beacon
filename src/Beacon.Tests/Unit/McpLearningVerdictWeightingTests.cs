using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part B — human-verdict weighting in pattern mining (§ Part B). Execution success is not correctness:
/// a signal a human marked <see cref="McpUserVerdict.Incorrect"/> must be excluded from CommonQuery /
/// JoinPattern mining, and a group containing a curator's <see cref="McpUserVerdict.Correct"/> verdict (a project admin
/// of the project, or an Admin) is more trustworthy — it earns a confidence bonus and supplies the representative
/// exemplar; any other rater's Correct verdict weighs nothing. An example question comes only from an answer to a question
/// (an <c>ask</c> signal), on one line, never with characters that do not show. Drives the (now internal) detection
/// methods directly over a mocked context (no DB — §4.7).
/// </summary>
[TestFixture]
public class McpLearningVerdictWeightingTests
{
    private const int ProjectId = 1;
    private const int DataSourceId = 1;
    private const int CuratorId = 40;
    private const int AskerId = 41;

    private static readonly IReadOnlySet<int> NoCurators = new HashSet<int>();
    private static readonly IReadOnlySet<int> Curators = new HashSet<int> { CuratorId };

    [Test]
    public async Task DetectCommonQueries_ExcludesIncorrectVerdictSignals()
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);

        // Three otherwise-mineable signals, but all human-marked INCORRECT → all excluded → below the
        // 3-signal threshold → no pattern is mined.
        await Service().DetectCommonQueriesAsync(
            context, ProjectId, DataSourceId,
            [CommonSignal(McpUserVerdict.Incorrect), CommonSignal(McpUserVerdict.Incorrect), CommonSignal(McpUserVerdict.Incorrect)],
            NoCurators,
            new McpLearningAggregationService.ExtractionStats(),
            CancellationToken.None);

        captured.Should().BeEmpty("a query a human marked wrong must never become a learned 'common query' pattern");
    }

    [Test]
    public async Task DetectCommonQueries_UnsetVerdict_MinesPatternAtBaselineConfidence()
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);

        await Service().DetectCommonQueriesAsync(
            context, ProjectId, DataSourceId,
            [CommonSignal(McpUserVerdict.Unset), CommonSignal(McpUserVerdict.Unset), CommonSignal(McpUserVerdict.Unset)],
            NoCurators,
            new McpLearningAggregationService.ExtractionStats(),
            CancellationToken.None);

        captured.Should().ContainSingle();
        captured[0].Confidence.Should().BeApproximately(0.7, 0.0001,
            "baseline confidence = 0.4 + 3 × 0.1, with no human-verified bonus");
    }

    [Test]
    public async Task DetectCommonQueries_CuratorConfirmedGroup_GetsConfidenceBonusAndRepresentative()
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);

        await Service().DetectCommonQueriesAsync(
            context, ProjectId, DataSourceId,
            [
                CommonSignal(McpUserVerdict.Unset, "unset A"),
                CommonSignal(McpUserVerdict.Correct, "the confirmed question", ratedBy: CuratorId),
                CommonSignal(McpUserVerdict.Unset, "unset B")
            ],
            Curators,
            new McpLearningAggregationService.ExtractionStats(),
            CancellationToken.None);

        captured.Should().ContainSingle();
        captured[0].Confidence.Should().BeApproximately(0.9, 0.0001,
            "a curator-confirmed group earns +0.2 over the 0.7 execution-only baseline");
        captured[0].ExampleQuestion.Should().Be("the confirmed question",
            "the curator-confirmed signal is chosen as the representative exemplar");
    }

    // The asker rating its own answer, or a verdict recorded before raters were (no rater), weighs nothing.
    [TestCase(AskerId)]
    [TestCase(null)]
    public async Task DetectCommonQueries_ACorrectVerdictByANonCurator_WeighsNothing(int? ratedBy)
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);

        await Service().DetectCommonQueriesAsync(
            context, ProjectId, DataSourceId,
            [
                CommonSignal(McpUserVerdict.Unset, "unset A"),
                CommonSignal(McpUserVerdict.Correct, "the self-confirmed question", ratedBy: ratedBy),
                CommonSignal(McpUserVerdict.Unset, "unset B")
            ],
            Curators,
            new McpLearningAggregationService.ExtractionStats(),
            CancellationToken.None);

        captured.Should().ContainSingle();
        captured[0].Confidence.Should().BeApproximately(0.7, 0.0001, "no curator confirmed the group");
        captured[0].ExampleQuestion.Should().Be("unset A", "the non-curator's verdict earns its signal no preference");
    }

    // A query signal's "question" is the SQL it ran: it never becomes an example question.
    [Test]
    public async Task DetectCommonQueries_ARepresentativeThatAnswersNoQuestion_GivesNoExampleQuestion()
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);
        var signals = new List<McpQuerySignal>
        {
            CommonSignal(McpUserVerdict.Unset, "SELECT id FROM public.orders"),
            CommonSignal(McpUserVerdict.Unset, "SELECT id FROM public.orders"),
            CommonSignal(McpUserVerdict.Unset, "SELECT id FROM public.orders")
        };
        foreach (var signal in signals)
        {
            signal.Tool = "query";
        }

        await Service().DetectCommonQueriesAsync(
            context, ProjectId, DataSourceId, signals, NoCurators, new McpLearningAggregationService.ExtractionStats(), CancellationToken.None);

        captured.Should().ContainSingle().Which.ExampleQuestion.Should().BeNull();
    }

    // An example question is stored as a prompt shows it: on one line, never with characters that do not show.
    [TestCase("orders\nlast week", "orders last week")]
    [TestCase("orders last\r\n-- week", "orders last  -- week")]
    [TestCase("orders \u202Elast week", null)]
    public async Task DetectCommonQueries_StoresTheExampleQuestionAsAPromptShowsIt(string question, string? stored)
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);

        await Service().DetectCommonQueriesAsync(
            context, ProjectId, DataSourceId,
            [CommonSignal(McpUserVerdict.Unset, question), CommonSignal(McpUserVerdict.Unset, question), CommonSignal(McpUserVerdict.Unset, question)],
            NoCurators,
            new McpLearningAggregationService.ExtractionStats(),
            CancellationToken.None);

        captured.Should().ContainSingle().Which.ExampleQuestion.Should().Be(stored);
    }

    [Test]
    public async Task DetectJoinPatterns_ExcludesIncorrectVerdictSignals()
    {
        var captured = new List<McpLearnedPattern>();
        var context = BuildContext(captured);

        await Service().DetectJoinPatternsAsync(
            context, ProjectId, DataSourceId,
            [JoinSignal(McpUserVerdict.Incorrect), JoinSignal(McpUserVerdict.Incorrect)],
            retainContent: true,
            NoCurators,
            new McpLearningAggregationService.ExtractionStats(),
            CancellationToken.None);

        captured.Should().BeEmpty("join signals a human marked wrong must not be mined into a join pattern");
    }

    private static McpQuerySignal CommonSignal(McpUserVerdict verdict, string question = "orders last week", int? ratedBy = null) =>
        new()
        {
            Tool = "ask",
            Question = question,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            GeneratedSql = "SELECT id FROM public.orders",
            TablesUsed = "[\"public.orders\"]",
            IsSuccessful = true,
            UserVerdict = verdict,
            FeedbackByUserId = ratedBy
        };

    private static McpQuerySignal JoinSignal(McpUserVerdict verdict) =>
        new()
        {
            Tool = "ask",
            Question = "orders with their customers",
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            GeneratedSql = "SELECT * FROM public.orders o JOIN public.customers c ON c.id = o.customer_id",
            TablesUsed = "[\"public.orders\",\"public.customers\"]",
            IsSuccessful = true,
            UserVerdict = verdict
        };

    private static McpLearningAggregationService Service()
    {
        return new McpLearningAggregationService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance);
    }

    private static DetectionTestContext BuildContext(List<McpLearnedPattern> captured)
    {
        var patternSet = BuildDbSet(Array.Empty<McpLearnedPattern>());
        patternSet
            .Setup(x => x.Add(It.IsAny<McpLearnedPattern>()))
            .Callback<McpLearnedPattern>(captured.Add);
        return new DetectionTestContext(patternSet.Object);
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

    private sealed class DetectionTestContext : BeaconContext
    {
        private static readonly DbContextOptions<DetectionTestContext> Options =
            new DbContextOptionsBuilder<DetectionTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpLearnedPattern> _patterns;

        public DetectionTestContext(DbSet<McpLearnedPattern> patterns) : base(Options, "beacon") => _patterns = patterns;

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
