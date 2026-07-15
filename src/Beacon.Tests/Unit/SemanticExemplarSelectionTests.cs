using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the B6 DAIL-SQL semantic few-shot exemplar selection in
/// <see cref="KnowledgeGraphService.GetRelevantPatternsAsync"/>. Exercises the real selection logic
/// against a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no DB, no
/// forbidden <c>UseInMemoryDatabase</c> — §4.7). The PostgreSQL raw <c>&lt;=&gt;</c> path cannot run
/// without a live pgvector DB, so the context spoofs a non-Npgsql provider name and the deterministic
/// in-memory cosine path is exercised with <see cref="FakeEmbeddingService"/>.
/// </summary>
[TestFixture]
public class SemanticExemplarSelectionTests
{
    private const int DataSourceId = 1;
    private const string Question = "How many orders were placed in 2023?";

    private static readonly List<string> TableNames = ["public.orders"];

    // Five CommonQuery exemplars all pointing at the same table (so table overlap is identical for all)
    // with STRICTLY DESCENDING confidence by id. Overlap/confidence ranking would therefore order them
    // 1,2,3,4,5; the semantic path must reorder them by embedding similarity to the masked question.
    private static readonly double[] ExemplarConfidenceById = [0, 0.95, 0.85, 0.75, 0.65, 0.55];

    [Test]
    public async Task GetRelevantPatterns_SemanticPath_SelectsTopKExemplarsOrderedBySimilarityNotOverlap()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // Exemplar #5 (LOWEST confidence) stores a vector identical to the masked-question vector — cosine 1.0,
        // guaranteed nearest. If selection were by overlap/confidence it would rank LAST; semantic must put it FIRST.
        var storedVectorById = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("annual revenue by region", CancellationToken.None),
            [2] = await embedder.EmbedAsync("list active users last login", CancellationToken.None),
            [3] = await embedder.EmbedAsync("average shipment delay per carrier", CancellationToken.None),
            [4] = await embedder.EmbedAsync("count distinct products in catalog", CancellationToken.None),
            [5] = queryVector
        };

        const int topK = 3;

        // Independently compute the expected similarity order (this is the "truth" the service must agree with).
        var expectedOrderedIds = storedVectorById
            .Select(x => (Id: x.Key, Score: EmbeddingCodec.Cosine(queryVector, x.Value)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Id)
            .Take(topK)
            .ToList();

        var patterns = BuildExemplars(storedVectorById.Keys);
        patterns.Add(NewCorrection(10));

        var embeddings = storedVectorById
            .Select(x => NewExemplarEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildService(embedder, patterns, embeddings, semanticEnabled: true, exemplarTopK: topK);

        var result = await service.GetRelevantPatternsAsync(
            DataSourceId, TableNames, Question, ct: CancellationToken.None);

        var commonQueries = result.Where(x => x.PatternType == nameof(McpPatternType.CommonQuery)).ToList();

        // Exactly topK exemplars, and they are the semantically-nearest ones in similarity order — not the
        // top-confidence ones and not in confidence order.
        commonQueries.Should().HaveCount(topK);
        commonQueries.Select(SqlToId).Should().Equal(expectedOrderedIds,
            "semantic selection must return the top-K nearest exemplars in descending-similarity order");

        // The identical-vector exemplar (#5) is nearest and MUST lead despite having the lowest confidence,
        // which is impossible under the table-overlap/confidence ranking.
        SqlToId(commonQueries[0]).Should().Be(5);

        // Corrections are NOT dropped — the SchemaCorrection is merged in AFTER the few-shot demos.
        result.Should().Contain(x => x.PatternType == nameof(McpPatternType.SchemaCorrection));
        var correctionIndex = result.FindIndex(x => x.PatternType == nameof(McpPatternType.SchemaCorrection));
        var lastExemplarIndex = result.FindLastIndex(x => x.PatternType == nameof(McpPatternType.CommonQuery));
        correctionIndex.Should().BeGreaterThan(lastExemplarIndex,
            "semantic few-shot exemplars lead, then the table-relevant corrections follow");
    }

    [Test]
    public async Task GetRelevantPatterns_WhenEmbedderUnavailable_FallsBackToTableOverlapSelection()
    {
        // Same data, but an unavailable embedder must fall back to the historical table-overlap ranking:
        // SchemaCorrection first (highest type priority), then ALL CommonQuery exemplars by confidence desc.
        var storedVectorById = new Dictionary<int, float[]>
        {
            [1] = new float[384],
            [2] = new float[384],
            [3] = new float[384],
            [4] = new float[384],
            [5] = new float[384]
        };

        var patterns = BuildExemplars(storedVectorById.Keys);
        patterns.Add(NewCorrection(10));

        var embeddings = storedVectorById
            .Select(x => NewExemplarEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildService(
            new UnavailableEmbeddingService(), patterns, embeddings, semanticEnabled: true, exemplarTopK: 3);

        var result = await service.GetRelevantPatternsAsync(
            DataSourceId, TableNames, Question, ct: CancellationToken.None);

        // Overlap ranking returns the correction first (type priority 4 beats CommonQuery's 1).
        result[0].PatternType.Should().Be(nameof(McpPatternType.SchemaCorrection));

        var commonQueries = result.Where(x => x.PatternType == nameof(McpPatternType.CommonQuery)).ToList();

        // ALL five exemplars survive (fallback is NOT capped to topK) and are ordered by confidence desc
        // (1,2,3,4,5) — i.e. the lowest-confidence #5 is LAST, the opposite of the semantic path.
        commonQueries.Should().HaveCount(5);
        commonQueries.Select(SqlToId).Should().Equal(1, 2, 3, 4, 5);
    }

    [Test]
    public async Task GetRelevantPatterns_WhenSemanticDisabled_FallsBackToTableOverlapSelection()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // #5 is the nearest by vector, but with semantic retrieval disabled it must still rank LAST (lowest
        // confidence) — proving the flag gates the semantic path.
        var storedVectorById = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("filler one", CancellationToken.None),
            [2] = await embedder.EmbedAsync("filler two", CancellationToken.None),
            [3] = await embedder.EmbedAsync("filler three", CancellationToken.None),
            [4] = await embedder.EmbedAsync("filler four", CancellationToken.None),
            [5] = queryVector
        };

        var patterns = BuildExemplars(storedVectorById.Keys);
        var embeddings = storedVectorById
            .Select(x => NewExemplarEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildService(embedder, patterns, embeddings, semanticEnabled: false, exemplarTopK: 3);

        var result = await service.GetRelevantPatternsAsync(
            DataSourceId, TableNames, Question, ct: CancellationToken.None);

        var commonQueries = result.Where(x => x.PatternType == nameof(McpPatternType.CommonQuery)).ToList();
        commonQueries.Should().HaveCount(5);
        commonQueries.Select(SqlToId).Should().Equal(1, 2, 3, 4, 5);
    }

    // ExampleSql is "SELECT {id}" so the returned few-shot demos can be mapped back to their pattern id.
    private static int SqlToId(LearnedPatternInfo info) =>
        int.Parse(info.ExampleSql!["SELECT ".Length..]);

    private static List<McpLearnedPattern> BuildExemplars(IEnumerable<int> ids) =>
        ids.Select(id => new McpLearnedPattern
        {
            Id = id,
            DataSourceId = DataSourceId,
            ProjectId = 1,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.CommonQuery,
            PatternContent = $"common query {id}",
            ExampleQuestion = $"example question {id}",
            ExampleSql = $"SELECT {id}",
            Confidence = ExemplarConfidenceById[id],
            Status = McpPatternStatus.Approved
        }).ToList();

    private static McpLearnedPattern NewCorrection(int id) =>
        new()
        {
            Id = id,
            DataSourceId = DataSourceId,
            ProjectId = 1,
            SchemaName = "public",
            TableName = "orders",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = "use created_at, not created_on",
            Confidence = 0.99,
            Status = McpPatternStatus.Approved
        };

    private static McpEmbedding NewExemplarEmbedding(int ownerId, float[] vector) =>
        new()
        {
            Id = 1000 + ownerId,
            DataSourceId = DataSourceId,
            OwnerType = McpEmbeddingOwnerType.Exemplar,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };

    private static KnowledgeGraphService BuildService(
        IBeaconEmbeddingService embedder,
        List<McpLearnedPattern> patterns,
        List<McpEmbedding> embeddings,
        bool semanticEnabled,
        int exemplarTopK)
    {
        var context = new SemanticTestContext(
            BuildDbSet(patterns).Object,
            BuildDbSet(embeddings).Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData
            {
                EnableSemanticRetrieval = semanticEnabled,
                ExemplarTopK = exemplarTopK
            });

        return new KnowledgeGraphService(
            factory.Object,
            settings.Object,
            embedder,
            NullLogger<KnowledgeGraphService>.Instance);
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
    /// A <see cref="BeaconContext"/> whose learned-pattern / embedding sets resolve to the supplied mocked
    /// sets, and whose <c>Database.ProviderName</c> reports a NON-Npgsql provider so the deterministic
    /// in-memory cosine retrieval path is exercised (the raw pgvector <c>&lt;=&gt;</c> path needs a live DB).
    /// </summary>
    private sealed class SemanticTestContext : BeaconContext
    {
        private static readonly DbContextOptions<SemanticTestContext> Options =
            new DbContextOptionsBuilder<SemanticTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpLearnedPattern> _patterns;
        private readonly DbSet<McpEmbedding> _embeddings;
        private DatabaseFacade? _database;

        public SemanticTestContext(DbSet<McpLearnedPattern> patterns, DbSet<McpEmbedding> embeddings)
            : base(Options, "beacon")
        {
            _patterns = patterns;
            _embeddings = embeddings;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpLearnedPattern))
            {
                return (DbSet<TEntity>)(object)_patterns;
            }

            if (typeof(TEntity) == typeof(McpEmbedding))
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class NonNpgsqlDatabaseFacade : DatabaseFacade
    {
        public NonNpgsqlDatabaseFacade(DbContext context) : base(context) { }

        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
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
}
