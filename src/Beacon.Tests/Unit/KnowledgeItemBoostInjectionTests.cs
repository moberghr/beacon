using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The bounded rank boost inside the ask context (<see cref="KnowledgeGraphService"/>): with <c>EnableKnowledgeItemBoost</c>
/// off the stats table is not read and the context is what it was; on, the project's own stats reorder the golden cases
/// the budget kept, the ranked learned patterns before their budget and the glossary terms retrieval found, and the
/// recorded ranks follow the rendered order. Runs the real service over a mocked <see cref="BeaconContext"/> backed by the
/// async-queryable doubles (§4.7), and the stats read on both providers through <see cref="SqlCapture"/>.
/// </summary>
[TestFixture]
[SetCulture("")]
public class KnowledgeItemBoostInjectionTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;
    private const int OtherProjectId = 99;
    private const string Question = "How many orders were refunded last month?";
    private const string Dialect = "PostgreSQL";

    // ---------- off ----------

    [Test]
    public async Task GoldenCases_BoostOff_ReadsNoStats_AndKeepTheSimilarityOrder()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            cases: scenario.Cases,
            stats: [Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 40)]);
        var settings = GoldenSettings(boost: false);

        var block = await Service(embedder, context, settings).BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(0, "with the boost off the stats table is not read");
        block.Items.Select(x => x.ItemId).Should().Equal(scenario.Order);
        CaseIdsIn(block.Text).Should().Equal(scenario.Order);
        block.Text.Should().Be(await BaselineGoldenTextAsync(embedder, scenario, settings));
    }

    [Test]
    public async Task GlossaryTerms_BoostOff_ReadsNoStats_AndKeepTheSimilarityOrder()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GlossaryScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            terms: scenario.Terms,
            stats: [Stat(ProjectId, McpKnowledgeItemKind.GlossaryTerm, scenario.Order[2], helpful: 40)]);
        var settings = GlossarySettings(boost: false);

        var block = await Service(embedder, context, settings).BuildGlossaryBlockWithItemsAsync(ProjectId, Question, settings, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(0);
        block.Items.Select(x => x.ItemId).Should().Equal(scenario.Order);
        TermIdsIn(block.Text).Should().Equal(scenario.Order);
    }

    [Test]
    public async Task LearnedPatterns_BoostOff_ReadsNoStats_AndKeepTheRanking()
    {
        var context = new BoostTestContext([], patterns: RankedPatterns(), stats: [Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 803, helpful: 40)]);
        var settings = PatternSettings(boost: false);

        var patterns = await Service(new FakeEmbeddingService(), context, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(0);
        patterns.Select(x => x.Id).Should().Equal(801, 802, 803);
    }

    // With the project's learning switched off the learning job no longer recomputes the counters, so they may be stale:
    // the boost, though on, reads no stats and every block keeps its retrieval order.
    [Test]
    public async Task GoldenCases_BoostOnButLearningOff_ReadsNoStats_AndKeepTheSimilarityOrder()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            cases: scenario.Cases,
            stats: [Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 40)]);
        var settings = GoldenSettings(boost: true);
        settings.EnableLearning = false;

        var block = await Service(embedder, context, settings).BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(0, "with learning off the stats table is not read");
        block.Items.Select(x => x.ItemId).Should().Equal(scenario.Order);
        CaseIdsIn(block.Text).Should().Equal(scenario.Order);
    }

    [Test]
    public async Task LearnedPatterns_BoostOnButLearningOff_ReadsNoStats_AndKeepTheRanking()
    {
        var context = new BoostTestContext([], patterns: RankedPatterns(), stats: [Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 803, helpful: 40)]);
        var settings = PatternSettings(boost: true);
        settings.EnableLearning = false;

        var patterns = await Service(new FakeEmbeddingService(), context, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(0);
        patterns.Select(x => x.Id).Should().Equal(801, 802, 803);
    }

    // A single selected item has nothing to be reordered against: the stats are not read.
    [Test]
    public async Task LearnedPatterns_BoostOn_ASinglePattern_ReadsNoStats()
    {
        var context = new BoostTestContext(
            [],
            patterns: RankedPatterns().Take(1).ToList(),
            stats: [Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 801, helpful: 40)]);
        var settings = PatternSettings(boost: true);

        var patterns = await Service(new FakeEmbeddingService(), context, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(0, "one item is never reordered, so its stats are not read");
        patterns.Select(x => x.Id).Should().Equal(801);
    }

    // The whole ask context: with the boost off, stats that would reorder it change nothing, byte for byte, and are not read.
    [Test]
    public async Task SmartContext_BoostOff_IsByteIdenticalToAContextWithoutStats_AndReadsNone()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var stats = new List<McpKnowledgeItemStat> { Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 40) };
        var withStats = AskContext(scenario, stats);
        var withoutStats = AskContext(scenario, []);
        var boostedContext = AskContext(scenario, stats);

        var off = await Service(embedder, withStats, AskSettings(boost: false)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var baseline = await Service(embedder, withoutStats, AskSettings(boost: false)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var on = await Service(embedder, boostedContext, AskSettings(boost: true)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        withStats.StatsReads.Should().Be(0);
        off.FullContext.Should().Be(baseline.FullContext);
        off.KnowledgeItems.Should().Equal(baseline.KnowledgeItems);
        on.FullContext.Should().NotBe(off.FullContext, "the same stats reorder the context once the boost is on, so the comparison above is not vacuous");
    }

    // ---------- on ----------

    // net 40 / 44 = 0.91: the third case's key 2 - 1.82 = 0.18 passes the second case, not the first.
    [Test]
    public async Task GoldenCases_BoostOn_EnoughEvidence_MovesTheCaseUp_AndTheRanksFollowTheText()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            cases: scenario.Cases,
            stats: [Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 40)]);
        var settings = GoldenSettings(boost: true);

        var block = await Service(embedder, context, settings).BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        List<int> expected = [scenario.Order[0], scenario.Order[2], scenario.Order[1], scenario.Order[3]];
        context.StatsReads.Should().Be(1, "one read of the stats for the block");
        CaseIdsIn(block.Text).Should().Equal(expected);
        block.Items.Select(x => x.ItemId).Should().Equal(expected, "the recorded items follow the rendered order");
        block.Items.Select(x => x.Rank).Should().Equal(1, 2, 3, 4);
        block.Items.Should().OnlyContain(x => x.Kind == McpKnowledgeItemKind.GoldenCase);
    }

    // The strongest evidence moves a case 2 places at most. The keys alone would put the harmful first case last, 3 places
    // down, passed by the helpful third and fourth and the neutral second; it is held 2 places down, before the fourth.
    [Test]
    public async Task GoldenCases_BoostOn_StrongEvidence_MovesEachCaseAtMostTwoPlaces()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            cases: scenario.Cases,
            stats:
            [
                Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[0], helpful: 0, harmful: 1e6),
                Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 1e6),
                Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[3], helpful: 1e6)
            ]);
        var settings = GoldenSettings(boost: true);

        var block = await Service(embedder, context, settings).BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        var rendered = CaseIdsIn(block.Text);
        rendered.Should().Equal(scenario.Order[2], scenario.Order[1], scenario.Order[0], scenario.Order[3]);
        foreach (var caseId in scenario.Order)
        {
            Math.Abs(rendered.IndexOf(caseId) - scenario.Order.IndexOf(caseId)).Should().BeLessThanOrEqualTo(2);
        }

        block.Items.Select(x => x.ItemId).Should().Equal(rendered);
    }

    [TestCase(4, 3)]
    [TestCase(40, 1)]
    public async Task GoldenCases_BoostOn_EvidenceBelowTheFloor_KeepsTheSimilarityOrder(int signals, int callers)
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            cases: scenario.Cases,
            stats: [Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 1e6, signals: signals, callers: callers)]);
        var settings = GoldenSettings(boost: true);

        var block = await Service(embedder, context, settings).BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(1);
        block.Items.Select(x => x.ItemId).Should().Equal(scenario.Order);
        block.Text.Should().Be(await BaselineGoldenTextAsync(embedder, scenario, settings));
    }

    // Only the caller's project's rows of the block's kind count: another project's stats of the same ids, or this project's
    // stats of another kind with the same ids, move nothing.
    [Test]
    public async Task GoldenCases_BoostOn_AnotherProjectsOrAnotherKindsStats_MoveNothing()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var stats = scenario.Order
            .Select(x => Stat(OtherProjectId, McpKnowledgeItemKind.GoldenCase, x, helpful: x == scenario.Order[3] ? 1e6 : 0, harmful: x == scenario.Order[0] ? 1e6 : 0))
            .Concat(scenario.Order.Select(x => Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, x, helpful: x == scenario.Order[3] ? 1e6 : 0, harmful: x == scenario.Order[0] ? 1e6 : 0)))
            .Concat(scenario.Order.Select(x => Stat(ProjectId, McpKnowledgeItemKind.GlossaryTerm, x, helpful: x == scenario.Order[3] ? 1e6 : 0, harmful: x == scenario.Order[0] ? 1e6 : 0)))
            .ToList();
        var context = new BoostTestContext(scenario.Embeddings, cases: scenario.Cases, stats: stats);
        var settings = GoldenSettings(boost: true);

        var block = await Service(embedder, context, settings).BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(1);
        block.Items.Select(x => x.ItemId).Should().Equal(scenario.Order);
        block.Text.Should().Be(await BaselineGoldenTextAsync(embedder, scenario, settings));
    }

    // The budget chooses the cases in similarity order, as before; the boost reorders only those: a case the budget left
    // out stays out however strong its evidence, and the cases shown are the same.
    [Test]
    public async Task GoldenCases_BoostOn_TheBudgetChoosesTheCases_TheBoostOnlyReordersThem()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var unlimited = await BaselineGoldenTextAsync(embedder, scenario, GoldenSettings(boost: false));
        var headingChars = unlimited.IndexOf("-- Q:", StringComparison.Ordinal);
        var entryChars = (unlimited.Length - headingChars) / scenario.Order.Count;
        var budget = headingChars + 2 * entryChars + entryChars / 2;
        var stats = new List<McpKnowledgeItemStat>
        {
            Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[1], helpful: 40),
            Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 1e6),
            Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[3], helpful: 1e6)
        };
        var offSettings = GoldenSettings(boost: false, budgetChars: budget);
        var onSettings = GoldenSettings(boost: true, budgetChars: budget);

        var off = await Service(embedder, new BoostTestContext(scenario.Embeddings, cases: scenario.Cases, stats: stats), offSettings)
            .BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, offSettings, Dialect, CancellationToken.None);
        var on = await Service(embedder, new BoostTestContext(scenario.Embeddings, cases: scenario.Cases, stats: stats), onSettings)
            .BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, onSettings, Dialect, CancellationToken.None);

        off.Items.Select(x => x.ItemId).Should().Equal(scenario.Order[0], scenario.Order[1]);
        on.Items.Select(x => x.ItemId).Should().Equal(scenario.Order[1], scenario.Order[0]);
        CaseIdsIn(on.Text).Should().Equal(scenario.Order[1], scenario.Order[0]);
        on.Text.Length.Should().Be(off.Text.Length, "the same cases are shown");
    }

    [Test]
    public async Task GlossaryTerms_BoostOn_EnoughEvidence_MovesTheTermUp_AndTheRanksFollowTheText()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GlossaryScenarioAsync(embedder);
        var context = new BoostTestContext(
            scenario.Embeddings,
            terms: scenario.Terms,
            stats:
            [
                Stat(ProjectId, McpKnowledgeItemKind.GlossaryTerm, scenario.Order[2], helpful: 40),
                Stat(OtherProjectId, McpKnowledgeItemKind.GlossaryTerm, scenario.Order[1], helpful: 1e6)
            ]);
        var settings = GlossarySettings(boost: true);

        var block = await Service(embedder, context, settings).BuildGlossaryBlockWithItemsAsync(ProjectId, Question, settings, Dialect, CancellationToken.None);

        List<int> expected = [scenario.Order[0], scenario.Order[2], scenario.Order[1]];
        context.StatsReads.Should().Be(1);
        TermIdsIn(block.Text).Should().Equal(expected);
        block.Items.Select(x => x.ItemId).Should().Equal(expected);
        block.Items.Select(x => x.Rank).Should().Equal(1, 2, 3);
    }

    // The overlap ranking orders 801, 802, 803 (same table and type, falling confidence); 803's evidence moves it up one.
    [Test]
    public async Task LearnedPatterns_BoostOn_EnoughEvidence_ReordersTheRanking_AndTheRanksFollowTheText()
    {
        var context = new BoostTestContext(
            [],
            patterns: RankedPatterns(),
            stats:
            [
                Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 803, helpful: 40),
                Stat(OtherProjectId, McpKnowledgeItemKind.LearnedPattern, 802, helpful: 1e6),
                Stat(OtherProjectId, McpKnowledgeItemKind.LearnedPattern, 804, helpful: 1e6)
            ]);
        var settings = PatternSettings(boost: true);

        var patterns = await Service(new FakeEmbeddingService(), context, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);

        context.StatsReads.Should().Be(1);
        patterns.Select(x => x.Id).Should().Equal(801, 803, 802);
        var text = KnowledgeGraphService.FormatLearnedPatternsForLlm(patterns);
        var renderedOrder = new[] { 801, 802, 803 }
            .OrderBy(x => text.IndexOf($"pattern {x} content", StringComparison.Ordinal))
            .ToList();
        renderedOrder.Should().Equal(801, 803, 802);
        KnowledgeGraphService.LearnedPatternItems(patterns)
            .Select(x => (x.ItemId, x.Rank))
            .Should()
            .Equal((801, 1), (803, 2), (802, 3));
    }

    // With semantic retrieval on the patterns take the exemplar-similarity order, set here to 803, 802, 801 (the reverse
    // of the overlap ranking, so the overlap fallback cannot pass this test); 801's evidence moves it up one place.
    [Test]
    public async Task LearnedPatterns_BoostOn_SemanticRetrieval_ReordersTheSemanticRanking()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await QueryVectorAsync(embedder);
        var vectors = new List<float[]>();
        foreach (var text in new[] { "refunded orders count", "revenue by region", "late shipments by carrier" })
        {
            vectors.Add(await embedder.EmbedAsync(text, CancellationToken.None));
        }

        var nearestFirst = vectors
            .OrderByDescending(x => EmbeddingCodec.Cosine(queryVector, x))
            .ToList();
        var embeddings = new List<McpEmbedding>
        {
            NewEmbedding(803, McpEmbeddingOwnerType.Exemplar, 1, nearestFirst[0], ProjectId),
            NewEmbedding(802, McpEmbeddingOwnerType.Exemplar, 2, nearestFirst[1], ProjectId),
            NewEmbedding(801, McpEmbeddingOwnerType.Exemplar, 3, nearestFirst[2], ProjectId)
        };
        var settings = PatternSettings(boost: true);
        settings.EnableSemanticRetrieval = true;
        settings.ExemplarTopK = 5;
        var withoutStats = new BoostTestContext(embeddings, patterns: RankedPatterns());
        var context = new BoostTestContext(
            embeddings,
            patterns: RankedPatterns(),
            stats:
            [
                Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 801, helpful: 40),
                Stat(OtherProjectId, McpKnowledgeItemKind.LearnedPattern, 802, helpful: 1e6)
            ]);

        var semantic = await Service(embedder, withoutStats, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);
        var patterns = await Service(embedder, context, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);

        semantic.Select(x => x.Id).Should().Equal([803, 802, 801], "without stats the semantic ranking stands");
        context.StatsReads.Should().Be(1);
        patterns.Select(x => x.Id).Should().Equal(803, 801, 802);
    }

    // The boost orders the ranked patterns before the budget, which then keeps the first ones as before: a pattern of
    // another project never enters, however strong its stats.
    [Test]
    public async Task LearnedPatterns_BoostOn_TheBudgetKeepsTheFirstOfTheReorderedRanking()
    {
        var stats = new List<McpKnowledgeItemStat>
        {
            Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 803, helpful: 40),
            Stat(ProjectId, McpKnowledgeItemKind.LearnedPattern, 804, helpful: 1e6),
            Stat(OtherProjectId, McpKnowledgeItemKind.LearnedPattern, 804, helpful: 1e6)
        };
        var offSettings = PatternSettings(boost: false);
        var onSettings = PatternSettings(boost: true);

        var off = await Service(new FakeEmbeddingService(), new BoostTestContext([], patterns: RankedPatterns(), stats: stats), offSettings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 2, budgetChars: 1_500, Dialect, CancellationToken.None);
        var on = await Service(new FakeEmbeddingService(), new BoostTestContext([], patterns: RankedPatterns(), stats: stats), onSettings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 2, budgetChars: 1_500, Dialect, CancellationToken.None);

        off.Select(x => x.Id).Should().Equal(801, 802);
        on.Select(x => x.Id).Should().Equal(801, 803);
    }

    // On the whole ask context the recorded golden items follow the order the context renders.
    [Test]
    public async Task SmartContext_BoostOn_RecordedRanksFollowTheRenderedOrder()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var context = AskContext(scenario, [Stat(ProjectId, McpKnowledgeItemKind.GoldenCase, scenario.Order[2], helpful: 40)]);

        var result = await Service(embedder, context, AskSettings(boost: true)).GetSmartContextForAskAsync(DataSourceId, ProjectId, Question, liveValueProbes: true, CancellationToken.None);

        List<int> expected = [scenario.Order[0], scenario.Order[2], scenario.Order[1], scenario.Order[3]];
        CaseIdsIn(result.FullContext).Should().Equal(expected);
        result.KnowledgeItems
            .Where(x => x.Kind == McpKnowledgeItemKind.GoldenCase)
            .Select(x => (x.ItemId, x.Rank))
            .Should()
            .Equal(expected.Select((x, y) => (x, y + 1)));
        result.KnowledgeItems
            .Where(x => x.Kind != McpKnowledgeItemKind.GoldenCase)
            .Should()
            .Equal(new KnowledgeItemRef(McpKnowledgeItemKind.LearnedPattern, 800, 1), new KnowledgeItemRef(McpKnowledgeItemKind.GlossaryTerm, 900, 1));
    }

    // ---------- failure ----------

    [Test]
    public async Task GoldenCases_BoostOn_TheStatsReadFails_KeepsTheSimilarityOrder_AndLogsIdsOnly()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GoldenScenarioAsync(embedder);
        var failure = new InvalidOperationException("stats read failed near secret-detail");
        var context = new BoostTestContext(scenario.Embeddings, cases: scenario.Cases, statsFailure: failure);
        var settings = GoldenSettings(boost: true);
        var logs = new LogRecorder();

        var block = await Service(embedder, context, settings, logs.For<KnowledgeGraphService>())
            .BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, settings, Dialect, CancellationToken.None);

        block.Items.Select(x => x.ItemId).Should().Equal(scenario.Order, "the ask goes on in retrieval order");
        block.Text.Should().Be(await BaselineGoldenTextAsync(embedder, scenario, settings));
        var warning = logs.Entries
            .Where(x => x.Level == LogLevel.Warning)
            .Should()
            .ContainSingle()
            .Subject;
        warning.Message.Should().Contain($"project {ProjectId}").And.Contain(nameof(McpKnowledgeItemKind.GoldenCase)).And.Contain(FailureCodes.Describe(failure));
        warning.Exception.Should().BeNull("a failure is logged by its codes, never as the exception object");
        warning.FullText.Should().NotContain("secret-detail");
    }

    [Test]
    public async Task GlossaryTerms_BoostOn_TheStatsReadIsCancelled_TheCancellationPropagates()
    {
        var embedder = new FakeEmbeddingService();
        var scenario = await GlossaryScenarioAsync(embedder);
        var context = new BoostTestContext(scenario.Embeddings, terms: scenario.Terms, statsFailure: new OperationCanceledException());
        var settings = GlossarySettings(boost: true);

        var build = () => Service(embedder, context, settings).BuildGlossaryBlockWithItemsAsync(ProjectId, Question, settings, Dialect, CancellationToken.None);

        await build.Should().ThrowAsync<OperationCanceledException>();
    }

    [Test]
    public async Task LearnedPatterns_BoostOn_TheStatsReadIsCancelled_TheCancellationPropagates()
    {
        var context = new BoostTestContext([], patterns: RankedPatterns(), statsFailure: new OperationCanceledException());
        var settings = PatternSettings(boost: true);

        var build = () => Service(new FakeEmbeddingService(), context, settings).GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);

        await build.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---------- the stats read on the providers ----------

    [Test]
    public async Task KnowledgeItemBoostStatRead_BoostOff_IsNotSent()
    {
        var capture = new SqlCapture().ThenRows(PatternRows());

        var patterns = await PatternsCapturingAsync(capture, boost: false);

        patterns.Select(x => x.Id).Should().Equal(801, 802, 803);
        capture.Commands.Should().ContainSingle("only the pattern bank is read");
        capture.Commands.Should().NotContain(x => x.Contains("mcp_knowledge_item_stats", StringComparison.Ordinal));
    }

    [Test]
    public async Task KnowledgeItemBoostStatRead_Translates()
    {
        var capture = new SqlCapture()
            .ThenRows(PatternRows())
            .ThenRows([803, 40.0, 0.0, 10, 3]);

        var patterns = await PatternsCapturingAsync(capture, boost: true);

        patterns.Select(x => x.Id).Should().Equal(801, 803, 802);
        capture.Commands.Should().HaveCount(2);
        var statRead = capture.Commands[1];
        statRead.Should().Contain("FROM beacon.mcp_knowledge_item_stats AS m");
        statRead.Should().Contain("m.project_id = @projectId").And.Contain("m.kind = @kind").And.Contain("m.item_id = ANY (@itemIds)");
        AssertSelectsOnlyTheEvidence(statRead, ["m.item_id", "m.helpful_score", "m.harmful_score", "m.signal_count", "m.caller_count"]);
        AssertStatReadParameters(capture.CommandParameters[1]);
    }

    [Test]
    public async Task KnowledgeItemBoostStatRead_SqlServer_Translates()
    {
        var capture = SqlCapture.OnSqlServer()
            .ThenRows(PatternRows())
            .ThenRows([803, 40.0, 0.0, 10, 3]);

        var patterns = await PatternsCapturingAsync(capture, boost: true);

        patterns.Select(x => x.Id).Should().Equal(801, 803, 802);
        capture.Commands.Should().HaveCount(2);
        var statRead = capture.Commands[1];
        statRead.Should().Contain("FROM [beacon].[McpKnowledgeItemStats] AS [m]");
        statRead.Should().Contain("[m].[ProjectId] = @projectId").And.Contain("[m].[Kind] = @kind").And.Contain("[m].[ItemId] IN (");
        AssertSelectsOnlyTheEvidence(statRead, ["[m].[ItemId]", "[m].[HelpfulScore]", "[m].[HarmfulScore]", "[m].[SignalCount]", "[m].[CallerCount]"]);
        capture.CommandParameters[1]["@projectId"].Should().Be(ProjectId);
        capture.CommandParameters[1]["@kind"].Should().Be((int)McpKnowledgeItemKind.LearnedPattern);
    }

    private static void AssertSelectsOnlyTheEvidence(string sql, IReadOnlyList<string> columns)
    {
        var selectList = sql[..sql.IndexOf("FROM", StringComparison.Ordinal)];
        foreach (var column in columns)
        {
            selectList.Should().Contain(column);
        }

        selectList.Split(',').Should().HaveCount(columns.Count, "the read projects only what the formula needs");
    }

    private static void AssertStatReadParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        parameters["@projectId"].Should().Be(ProjectId);
        parameters["@kind"].Should().Be((int)McpKnowledgeItemKind.LearnedPattern);
        parameters["@itemIds"].Should().BeEquivalentTo(new[] { 801, 802, 803 });
    }

    // The overlap ranking (semantic retrieval off) over the three patterns the capture answers, read on its provider.
    private static Task<List<LearnedPatternInfo>> PatternsCapturingAsync(SqlCapture capture, bool boost)
    {
        var settings = PatternSettings(boost);
        var settingsProvider = SettingsProviderMock.Create(settings);
        var service = new KnowledgeGraphService(
            capture.Factory(),
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            new FakeEmbeddingService(),
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance);

        return service.GetRelevantPatternsAsync(
            DataSourceId, ProjectId, ["public.orders"], Question, maxPatterns: 10, budgetChars: 1_500, Dialect, CancellationToken.None);
    }

    // The pattern bank's columns as its read selects them: id, type, table, schema, column, content, example question and
    // SQL, confidence.
    private static object[][] PatternRows()
    {
        return RankedPatterns()
            .Select(x => new object[] { x.Id, (int)x.PatternType, x.TableName, x.SchemaName, DBNull.Value, x.PatternContent, DBNull.Value, DBNull.Value, x.Confidence })
            .ToArray();
    }

    private static List<McpLearnedPattern> RankedPatterns()
    {
        return
        [
            NewPattern(801, ProjectId, confidence: 0.9),
            NewPattern(802, ProjectId, confidence: 0.8),
            NewPattern(803, ProjectId, confidence: 0.7)
        ];
    }

    private static McpLearnedPattern NewPattern(int id, int projectId, double confidence)
    {
        return new McpLearnedPattern
        {
            Id = id,
            DataSourceId = DataSourceId,
            ProjectId = projectId,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.JoinPattern,
            PatternContent = $"pattern {id} content",
            Status = McpPatternStatus.Approved,
            Confidence = confidence
        };
    }

    // Four golden cases of the project at different distances from the question, and their similarity order.
    private static async Task<GoldenScenario> GoldenScenarioAsync(FakeEmbeddingService embedder)
    {
        var queryVector = await QueryVectorAsync(embedder);
        var texts = new Dictionary<int, string>
        {
            [701] = "refunded orders count",
            [702] = "revenue by region",
            [703] = "active customers list",
            [704] = "late shipments by carrier"
        };
        var vectors = new Dictionary<int, float[]>();
        foreach (var text in texts)
        {
            vectors[text.Key] = await embedder.EmbedAsync(text.Value, CancellationToken.None);
        }

        var order = vectors
            .OrderByDescending(x => EmbeddingCodec.Cosine(queryVector, x.Value))
            .Select(x => x.Key)
            .ToList();
        var cases = texts.Keys
            .Select(x => NewCase(x, ProjectId))
            .ToList();
        var embeddings = vectors
            .Select(x => NewEmbedding(x.Key, McpEmbeddingOwnerType.GoldenCase, x.Key, x.Value, ProjectId))
            .ToList();

        return new GoldenScenario(cases, embeddings, order);
    }

    private static async Task<GlossaryScenario> GlossaryScenarioAsync(FakeEmbeddingService embedder)
    {
        var queryVector = await QueryVectorAsync(embedder);
        var texts = new Dictionary<int, string>
        {
            [901] = "refund count",
            [902] = "revenue by region",
            [903] = "active customers"
        };
        var vectors = new Dictionary<int, float[]>();
        foreach (var text in texts)
        {
            vectors[text.Key] = await embedder.EmbedAsync(text.Value, CancellationToken.None);
        }

        var order = vectors
            .OrderByDescending(x => EmbeddingCodec.Cosine(queryVector, x.Value))
            .Select(x => x.Key)
            .ToList();
        var terms = texts.Keys
            .Select(x => NewTerm(x, ProjectId))
            .ToList();
        var embeddings = vectors
            .Select(x => NewEmbedding(x.Key, McpEmbeddingOwnerType.GlossaryTerm, x.Key, x.Value, ProjectId, dataSourceId: 0))
            .ToList();

        return new GlossaryScenario(terms, embeddings, order);
    }

    // The block as the service renders it with no stats at all and the boost off.
    private static async Task<string> BaselineGoldenTextAsync(FakeEmbeddingService embedder, GoldenScenario scenario, McpSettingsData settings)
    {
        var baselineSettings = GoldenSettings(boost: false, budgetChars: settings.GoldenExemplarBudgetChars);
        var block = await Service(embedder, new BoostTestContext(scenario.Embeddings, cases: scenario.Cases), baselineSettings)
            .BuildGoldenExemplarBlockWithItemsAsync(DataSourceId, ProjectId, Question, baselineSettings, Dialect, CancellationToken.None);

        return block.Text;
    }

    // A small schema (the fast path) with the scenario's golden cases, one learned pattern and one glossary term.
    private static BoostTestContext AskContext(GoldenScenario scenario, List<McpKnowledgeItemStat> stats)
    {
        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "Sales DB",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "unused-in-test"
        };
        var pattern = NewPattern(800, ProjectId, confidence: 0.9);
        var embeddings = scenario.Embeddings
            .Append(NewEmbedding(900, McpEmbeddingOwnerType.GlossaryTerm, 900, new float[384], ProjectId, dataSourceId: 0))
            .ToList();

        return new BoostTestContext(
            embeddings,
            cases: scenario.Cases,
            terms: [NewTerm(900, ProjectId)],
            patterns: [pattern],
            stats: stats,
            dataSource: dataSource,
            metadata: [Table(dataSource, 1, "orders", ["id", "status"])]);
    }

    private static KnowledgeGraphService Service(IBeaconEmbeddingService embedder, BeaconContext context, McpSettingsData settings, ILogger<KnowledgeGraphService>? logger = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        var settingsProvider = SettingsProviderMock.Create(settings);

        return new KnowledgeGraphService(
            factory.Object,
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            embedder,
            Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>())
                == Task.FromResult(SchemaGraph.Build(Array.Empty<TableMetadataDto>(), Array.Empty<SchemaRelationshipEdge>()))),
            Mock.Of<IValueGroundingService>(x => x.BuildValueMatchesBlockAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<ValueGroundingTable>>(), It.IsAny<McpSettingsData>(), It.IsAny<CancellationToken>())
                == Task.FromResult("")),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            logger ?? NullLogger<KnowledgeGraphService>.Instance);
    }

    private static McpSettingsData GoldenSettings(bool boost, int budgetChars = 0)
    {
        return new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5,
            GoldenExemplarBudgetChars = budgetChars,
            EnableKnowledgeItemBoost = boost
        };
    }

    private static McpSettingsData GlossarySettings(bool boost)
    {
        return new McpSettingsData
        {
            EnableSemanticRetrieval = true,
            GlossaryTopK = 10,
            EnableKnowledgeItemBoost = boost
        };
    }

    // Semantic retrieval off: the patterns take the table-overlap ranking.
    private static McpSettingsData PatternSettings(bool boost)
    {
        return new McpSettingsData
        {
            EnableLearning = true,
            EnableSemanticRetrieval = false,
            EnableKnowledgeItemBoost = boost
        };
    }

    private static McpSettingsData AskSettings(bool boost)
    {
        return new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableLearning = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5,
            GoldenExemplarBudgetChars = 0,
            ExemplarTopK = 5,
            GlossaryTopK = 5,
            EnableKnowledgeItemBoost = boost
        };
    }

    private static McpKnowledgeItemStat Stat(int projectId, McpKnowledgeItemKind kind, int itemId, double helpful, double harmful = 0, int signals = 10, int callers = 3)
    {
        return new McpKnowledgeItemStat
        {
            ProjectId = projectId,
            Kind = kind,
            ItemId = itemId,
            HelpfulScore = helpful,
            HarmfulScore = harmful,
            SignalCount = signals,
            CallerCount = callers
        };
    }

    private static async Task<float[]> QueryVectorAsync(FakeEmbeddingService embedder)
    {
        return await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);
    }

    // The case ids whose gold SQL the text renders (each reads its own t_case_{id} table), in rendered order.
    private static List<int> CaseIdsIn(string text)
    {
        return Enumerable.Range(700, 10)
            .Where(x => text.Contains($"FROM t_case_{x}", StringComparison.Ordinal))
            .OrderBy(x => text.IndexOf($"FROM t_case_{x}", StringComparison.Ordinal))
            .ToList();
    }

    private static List<int> TermIdsIn(string text)
    {
        return Enumerable.Range(900, 10)
            .Where(x => text.Contains($"**term-{x}**", StringComparison.Ordinal))
            .OrderBy(x => text.IndexOf($"**term-{x}**", StringComparison.Ordinal))
            .ToList();
    }

    private static McpEvalCase NewCase(int id, int projectId)
    {
        return new McpEvalCase
        {
            Id = id,
            ProjectId = projectId,
            DataSourceId = DataSourceId,
            Question = $"question {id}",
            GoldSql = $"SELECT count(*) FROM t_case_{id}",
            IsActive = true
        };
    }

    private static McpGlossaryTerm NewTerm(int id, int projectId)
    {
        return new McpGlossaryTerm
        {
            Id = id,
            ProjectId = projectId,
            Term = $"term-{id}",
            Definition = $"definition {id}",
            IsActive = true
        };
    }

    private static McpEmbedding NewEmbedding(int ownerId, McpEmbeddingOwnerType ownerType, int id, float[] vector, int projectId, int dataSourceId = DataSourceId)
    {
        return new McpEmbedding
        {
            Id = id,
            DataSourceId = dataSourceId,
            ProjectId = projectId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };
    }

    private static DatabaseMetadata Table(DataSource dataSource, int id, string name, List<string> columns)
    {
        return new DatabaseMetadata
        {
            Id = id,
            DataSourceId = DataSourceId,
            DataSource = dataSource,
            SchemaName = "public",
            TableName = name,
            TableDescription = name,
            Columns = columns
                .Select((x, y) =>
                    new ColumnMetadata
                    {
                        Id = id * 1_000 + y,
                        ColumnName = x,
                        DataType = "integer",
                        IsPrimaryKey = y == 0,
                        IsNullable = y != 0
                    })
                .ToList()
        };
    }

    private static Mock<DbSet<T>> BuildDbSet<T>(List<T> data) where T : class
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
        set.Setup(x => x.AsQueryable()).Returns(() => set.Object);

        return set;
    }

    private sealed record GoldenScenario(List<McpEvalCase> Cases, List<McpEmbedding> Embeddings, List<int> Order);

    private sealed record GlossaryScenario(List<McpGlossaryTerm> Terms, List<McpEmbedding> Embeddings, List<int> Order);

    /// <summary>
    /// The grounding sets the ask context reads, plus the knowledge item stats: every access to the stats set is counted
    /// (<see cref="StatsReads"/>), and fails with <c>statsFailure</c> when one is given.
    /// </summary>
    private sealed class BoostTestContext : BeaconContext
    {
        private static readonly DbContextOptions<BoostTestContext> ContextOptions =
            new DbContextOptionsBuilder<BoostTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Dictionary<Type, object> _sets = [];
        private readonly Exception? _statsFailure;
        private DatabaseFacade? _database;
        private int _statsReads;

        public BoostTestContext(
            List<McpEmbedding> embeddings,
            List<McpEvalCase>? cases = null,
            List<McpGlossaryTerm>? terms = null,
            List<McpLearnedPattern>? patterns = null,
            List<McpKnowledgeItemStat>? stats = null,
            DataSource? dataSource = null,
            List<DatabaseMetadata>? metadata = null,
            Exception? statsFailure = null) : base(ContextOptions, "beacon")
        {
            _statsFailure = statsFailure;
            AddSet(embeddings);
            AddSet(cases ?? []);
            AddSet(terms ?? []);
            AddSet(patterns ?? []);
            AddSet(stats ?? []);
            AddSet(dataSource == null ? [] : new List<DataSource> { dataSource });
            AddSet(metadata ?? []);
            AddSet(dataSource == null ? [] : new List<ProjectDataSource> { new() { ProjectId = ProjectId, DataSourceId = dataSource.Id } });
            AddSet(new List<ColumnMetadata>());
            AddSet(new List<ProjectDocumentationSection>());
            AddSet(new List<McpDescriptionItem>());
        }

        public int StatsReads => Volatile.Read(ref _statsReads);

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpKnowledgeItemStat))
            {
                Interlocked.Increment(ref _statsReads);
                if (_statsFailure != null)
                {
                    throw _statsFailure;
                }
            }

            return _sets.TryGetValue(typeof(TEntity), out var set)
                ? (DbSet<TEntity>)set
                : base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private void AddSet<T>(List<T> rows) where T : class
        {
            _sets[typeof(T)] = BuildDbSet(rows).Object;
        }
    }

    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
