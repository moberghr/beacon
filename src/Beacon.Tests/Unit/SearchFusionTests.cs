using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Hybrid project search (the MCP <c>search</c> tool): the keyword and semantic arms are fused via the
/// REAL <see cref="ReciprocalRankFusion"/> helper through
/// <see cref="KnowledgeGraphService.FuseProjectSearchArms"/>, then re-ordered by the existing
/// deterministic paging sort (fused score as Relevance, then name tie-breaks). Also locks the fail-open
/// contract of <see cref="KnowledgeGraphService.ApplyProjectSemanticFusionAsync"/>: an unavailable or
/// throwing embedder returns the keyword-only list EXACTLY (order included), so the tool behaves as if
/// the semantic arm never existed.
/// </summary>
[TestFixture]
public class SearchFusionTests
{
    private const int ProjectId = 7;

    [Test]
    public void FuseProjectSearchArms_TwoRankedArms_ProducesExpectedMergedOrder()
    {
        // Keyword arm: alpha(1), bravo(2), charlie(3). Semantic arm: charlie(1), delta(2).
        // RRF (k=60): charlie = 1/63 + 1/61; alpha = 1/61; bravo = delta = 1/62 (tie -> name order).
        var alpha = MakeTable("alpha");
        var bravo = MakeTable("bravo");
        var charlie = MakeTable("charlie");
        var delta = MakeTable("delta");

        var fused = KnowledgeGraphService.FuseProjectSearchArms(
            [alpha, bravo, charlie],
            [charlie, delta],
            maxResults: 10);

        fused.Select(x => x.TableName).Should().Equal("charlie", "alpha", "bravo", "delta");
        fused[0].Relevance.Should().BeApproximately((1.0 / 63) + (1.0 / 61), 1e-9,
            "Relevance must be re-weighted with the fused RRF score");
    }

    [Test]
    public void FuseProjectSearchArms_ItemInBothArms_OutranksSingleArmTopHits()
    {
        // "shared" is only rank 2 in each arm; the rank-1 hits appear in one arm each.
        // Consensus wins: 1/62 + 1/62 > 1/61.
        var keywordTop = MakeTable("aaa_keyword_top");
        var semanticTop = MakeTable("aab_semantic_top");
        var shared = MakeTable("zzz_shared");

        var fused = KnowledgeGraphService.FuseProjectSearchArms(
            [keywordTop, shared],
            [semanticTop, shared],
            maxResults: 10);

        fused[0].TableName.Should().Be("zzz_shared",
            "an item ranked in both arms outranks single-arm items of similar rank despite losing every name tie-break");
    }

    [Test]
    public void FuseProjectSearchArms_EqualFusedScores_TieBreakByNameOrder()
    {
        // Four disjoint items, two per arm. Equal ranks across arms produce EQUAL fused scores, so the
        // deterministic paging tie-break (source, schema, table, column) must decide — not arm order.
        var keywordFirst = MakeTable("zeta");
        var keywordSecond = MakeTable("mike");
        var semanticFirst = MakeTable("alpha");
        var semanticSecond = MakeTable("nova");

        var fused = KnowledgeGraphService.FuseProjectSearchArms(
            [keywordFirst, keywordSecond],
            [semanticFirst, semanticSecond],
            maxResults: 10);

        fused.Select(x => x.TableName).Should().Equal("alpha", "zeta", "mike", "nova");
    }

    [Test]
    public async Task ApplyProjectSemanticFusion_EmbedderUnavailable_ReturnsKeywordOnlyExactly()
    {
        var keywordRanked = KeywordRankedList();
        var service = BuildService(new UnavailableEmbeddingService());
        await using var context = NpgsqlTestContext.Create();

        var result = await service.ApplyProjectSemanticFusionAsync(
            context, keywordRanked, "customer", ProjectId, [1, 2], maxResults: 5, CancellationToken.None);

        result.Should().BeSameAs(keywordRanked, "no embedder => the keyword-only result is returned unchanged");
        result.Select(x => x.TableName).Should().Equal("orders", "customers", "invoices");
    }

    [Test]
    public async Task ApplyProjectSemanticFusion_EmbedsTheRawUnmaskedQuery()
    {
        // Pins the documented decision in BuildProjectSemanticArmAsync: the project-search semantic
        // arm embeds the RAW keyword — metadata and doc chunks are embedded raw at index time, so a
        // masked query (EmbeddingMaskingHelper) would land in a different representation region.
        const string query = "orders for 'ACME Corp' since 2024-01-01";
        var recorder = new RecordingEmbeddingService();
        var service = BuildService(recorder);
        await using var context = NpgsqlTestContext.Create();

        await service.ApplyProjectSemanticFusionAsync(
            context, KeywordRankedList(), query, ProjectId, [1, 2], maxResults: 5, CancellationToken.None);

        recorder.ReceivedTexts.Should().ContainSingle("the semantic arm embeds the query exactly once")
            .Which.Should().Be(query, "the semantic arm must embed the raw query text, not the masked form");
        recorder.ReceivedTexts.Single().Should().NotBe(EmbeddingMaskingHelper.Mask(query),
            "the sentinel query must actually differ from its masked form for this test to prove anything");
    }

    [Test]
    public async Task ApplyProjectSemanticFusion_SemanticRetrievalDisabled_ReturnsKeywordOnlyExactly()
    {
        // The settings toggle must short-circuit BEFORE the semantic arm embeds anything, even when
        // a local embedder is available.
        var keywordRanked = KeywordRankedList();
        var recorder = new RecordingEmbeddingService();
        var service = BuildService(recorder, enableSemanticRetrieval: false);
        await using var context = NpgsqlTestContext.Create();

        var result = await service.ApplyProjectSemanticFusionAsync(
            context, keywordRanked, "customer", ProjectId, [1, 2], maxResults: 5, CancellationToken.None);

        result.Should().BeSameAs(keywordRanked, "EnableSemanticRetrieval=false => the keyword-only result is returned unchanged");
        recorder.ReceivedTexts.Should().BeEmpty("the semantic arm must not run at all when retrieval is disabled");
    }

    [Test]
    public async Task ApplyProjectSemanticFusion_EmbedderThrows_ReturnsKeywordOnlyExactly()
    {
        var keywordRanked = KeywordRankedList();
        var service = BuildService(new ThrowingEmbeddingService());
        await using var context = NpgsqlTestContext.Create();

        var result = await service.ApplyProjectSemanticFusionAsync(
            context, keywordRanked, "customer", ProjectId, [1, 2], maxResults: 5, CancellationToken.None);

        result.Should().BeSameAs(keywordRanked, "a semantic-arm failure must fail open onto the keyword-only result");
        result.Select(x => x.TableName).Should().Equal("orders", "customers", "invoices");
    }

    private static List<SearchResult> KeywordRankedList() =>
    [
        MakeTable("orders", relevance: 1.0),
        MakeTable("customers", relevance: 0.8),
        MakeTable("invoices", relevance: 0.8)
    ];

    [Test]
    public void FuseProjectSearchArms_TwoDocSectionsSharingA200CharPrefix_StayDistinctResults()
    {
        // codex PR-11 R4: documentation results dedupe on their stable DocIdentity, not on the truncated
        // 200-char Description preview — chunks of two DIFFERENT sections sharing a long common prefix
        // must NOT collapse into one. (Both arms emit "docsection:{sectionId}": the keyword arm the
        // section's own id, the semantic arm the chunk's SourceSectionId.)
        var sharedPrefix = new string('p', 200);
        var sectionOneChunk = MakeDoc("docsection:11", sharedPrefix);
        var sectionTwoChunk = MakeDoc("docsection:12", sharedPrefix);

        var fused = KnowledgeGraphService.FuseProjectSearchArms(
            keywordRanked: [],
            semanticRanked: [sectionOneChunk, sectionTwoChunk],
            maxResults: 10);

        fused.Should().HaveCount(2, "distinct sections with identical truncated previews must stay distinct");
        fused.Select(x => x.DocIdentity).Should().BeEquivalentTo(["docsection:11", "docsection:12"]);
    }

    [Test]
    public void FuseProjectSearchArms_SameDocIdentityInBothArms_FusesIntoOneResult()
    {
        // Cross-arm dedup is real, not vacuous: the keyword arm emits the section's own id and the
        // semantic arm the chunk's SourceSectionId, so a section retrieved by both arms shares one
        // "docsection:{id}" identity and must fuse into a single result.
        var keywordSide = MakeDoc("docsection:5", "how notifications work");
        var semanticSide = MakeDoc("docsection:5", "how notifications work (chunk of section 5)");

        var fused = KnowledgeGraphService.FuseProjectSearchArms(
            keywordRanked: [keywordSide],
            semanticRanked: [semanticSide],
            maxResults: 10);

        fused.Should().ContainSingle("the same section retrieved by both arms is one result");
        fused[0].DocIdentity.Should().Be("docsection:5");
    }

    private static SearchResult MakeDoc(string docIdentity, string description) =>
        new()
        {
            Type = "documentation",
            DataSourceName = "proj",
            SchemaName = string.Empty,
            TableName = string.Empty,
            Description = description,
            Relevance = 0.5,
            DocIdentity = docIdentity
        };

    private static SearchResult MakeTable(string tableName, double relevance = 0.8) =>
        new()
        {
            Type = "table",
            DataSourceId = 1,
            DataSourceName = "ds-1",
            SchemaName = "public",
            TableName = tableName,
            Description = null,
            Relevance = relevance
        };

    private static KnowledgeGraphService BuildService(IBeaconEmbeddingService embedder, bool enableSemanticRetrieval = true)
    {
        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData { EnableSemanticRetrieval = enableSemanticRetrieval });

        return new KnowledgeGraphService(
            new Mock<IDbContextFactory<BeaconContext>>().Object,
            settings.Object,
            embedder,
            Mock.Of<ISchemaGraphService>(),
            NullLogger<KnowledgeGraphService>.Instance);
    }

    /// <summary>Records every EmbedAsync input and returns a zero vector; the kNN step that follows
    /// fails on the dummy connection and the fail-open catch returns the keyword arm — the recorded
    /// text is captured before that.</summary>
    private sealed class RecordingEmbeddingService : IBeaconEmbeddingService
    {
        public List<string> ReceivedTexts { get; } = [];

        public int Dimensions => 384;

        public bool IsAvailable => true;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct)
        {
            ReceivedTexts.Add(text);
            return Task.FromResult(new float[Dimensions]);
        }

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            throw new NotSupportedException("project search embeds a single query");
    }

    private sealed class UnavailableEmbeddingService : IBeaconEmbeddingService
    {
        public int Dimensions => 384;

        public bool IsAvailable => false;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            throw new InvalidOperationException("Embeddings unavailable");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            throw new InvalidOperationException("Embeddings unavailable");
    }

    private sealed class ThrowingEmbeddingService : IBeaconEmbeddingService
    {
        public int Dimensions => 384;

        public bool IsAvailable => true;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
            throw new InvalidOperationException("Transient embedder failure");

        public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
            throw new InvalidOperationException("Transient embedder failure");
    }
}
