using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the T2-B4 retrieval-time selection + temporal decay (§ Architecture ⑧):
/// <list type="bullet">
/// <item>all-type semantic selection — <see cref="KnowledgeGraphService.GetRelevantPatternsAsync"/> now
/// picks the top-k nearest lessons of ANY type by masked-question cosine, not just CommonQuery and not in
/// table-overlap order;</item>
/// <item>stale filter — a superseded lesson is never injected even when it is the nearest by vector;</item>
/// <item>staleness detection — <see cref="McpLearningAggregationService.DetectStalePatternsAsync"/> marks a
/// SchemaCorrection stale when its referenced column is absent from current <c>ColumnMetadata</c>, while
/// leaving one whose column still exists untouched (and keeping the row — history is retained).</item>
/// </list>
/// All exercised against a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no DB,
/// no forbidden <c>UseInMemoryDatabase</c> — §4.7); the deterministic in-memory cosine path runs via a
/// non-Npgsql provider spoof + <see cref="FakeEmbeddingService"/>.
/// </summary>
[TestFixture]
public class PatternDecayAndSelectionTests
{
    private const int DataSourceId = 1;
    private const int ProjectId = 1;
    private const string Question = "How many orders were placed in 2023?";

    private static readonly List<string> TableNames = ["public.orders"];

    [Test]
    public async Task GetRelevantPatterns_SemanticPath_SelectsTopKAcrossAllTypesBySimilarity()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // Mixed pattern types, ALL with an exemplar embedding. #2 (a JoinPattern) stores a vector IDENTICAL
        // to the masked-question vector (cosine 1.0) → guaranteed nearest. Under the old CommonQuery-only
        // semantic path a JoinPattern could never be the top few-shot pick; under table-overlap ranking a
        // SchemaCorrection (type priority 4) would lead. So a JoinPattern leading proves cross-type selection
        // by pure similarity.
        var typeById = new Dictionary<int, McpPatternType>
        {
            [1] = McpPatternType.SchemaCorrection,
            [2] = McpPatternType.JoinPattern,
            [3] = McpPatternType.CommonQuery,
            [4] = McpPatternType.SchemaCorrection,
            [5] = McpPatternType.CommonQuery
        };

        var storedVectorById = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("schema correction one", CancellationToken.None),
            [2] = queryVector,
            [3] = await embedder.EmbedAsync("common query three", CancellationToken.None),
            [4] = await embedder.EmbedAsync("schema correction four", CancellationToken.None),
            [5] = await embedder.EmbedAsync("common query five", CancellationToken.None)
        };

        const int topK = 3;

        // Independently compute the expected similarity order — the "truth" the service must agree with.
        var expectedOrderedIds = storedVectorById
            .Select(x => (Id: x.Key, Score: EmbeddingCodec.Cosine(queryVector, x.Value)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Id)
            .Take(topK)
            .ToList();

        var patterns = typeById
            .Select(x => NewPattern(x.Key, x.Value, McpPatternStatus.Approved))
            .ToList();
        var embeddings = storedVectorById
            .Select(x => NewExemplarEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildKnowledgeService(embedder, patterns, embeddings, semanticEnabled: true, exemplarTopK: topK);

        var result = await service.GetRelevantPatternsAsync(
            DataSourceId, TableNames, Question, ct: CancellationToken.None);

        // Exactly the top-k nearest across ALL types, in descending-similarity order — no backfill because
        // every seeded lesson had an embedding, so nothing falls through to the table-overlap ranking.
        result.Should().HaveCount(topK);
        result.Select(SqlToId).Should().Equal(expectedOrderedIds,
            "semantic selection must return the top-k nearest lessons of any type in similarity order");

        // The identical-vector JoinPattern (#2) is nearest and MUST lead — impossible under CommonQuery-only
        // few-shot selection and under table-overlap ranking (which would lead with a SchemaCorrection).
        result[0].PatternType.Should().Be(nameof(McpPatternType.JoinPattern));
        SqlToId(result[0]).Should().Be(2);

        // The injected set genuinely spans more than one pattern type (not just CommonQuery).
        result.Select(x => x.PatternType).Distinct().Should().HaveCountGreaterThan(1);
    }

    [Test]
    public async Task GetRelevantPatterns_SupersededLesson_IsNeverInjected_EvenWhenNearestByVector()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // #1 is a VALID lesson (SupersededAt == null) with a non-identical vector. #2 is STALE
        // (SupersededAt set) yet stores the identical-to-query vector → it is the single nearest neighbour.
        // Temporal decay must exclude #2 from injection despite it being the top vector hit.
        var validPattern = NewPattern(1, McpPatternType.CommonQuery, McpPatternStatus.Approved);
        var stalePattern = NewPattern(2, McpPatternType.CommonQuery, McpPatternStatus.Approved);
        stalePattern.SupersededAt = DateTime.UtcNow;

        var embeddings = new List<McpEmbedding>
        {
            NewExemplarEmbedding(1, await embedder.EmbedAsync("some other valid lesson", CancellationToken.None)),
            NewExemplarEmbedding(2, queryVector)
        };

        var service = BuildKnowledgeService(
            embedder, [validPattern, stalePattern], embeddings, semanticEnabled: true, exemplarTopK: 3);

        var result = await service.GetRelevantPatternsAsync(
            DataSourceId, TableNames, Question, ct: CancellationToken.None);

        var returnedIds = result.Select(SqlToId).ToList();
        returnedIds.Should().NotContain(2, "a superseded lesson is never injected, even as the nearest vector hit");
        returnedIds.Should().Contain(1, "the valid (non-superseded) lesson is still selected");
    }

    [Test]
    public async Task DetectStalePatterns_MarksCorrectionStale_WhenColumnAbsent_LeavesExistingColumnUntouched()
    {
        var before = DateTime.UtcNow;

        // A: references a column that no longer exists → must go stale.
        var absentColumnCorrection = NewCorrection(1, "loans", "created_at");
        // B: references a column that still exists → must stay valid.
        var presentColumnCorrection = NewCorrection(2, "loans", "created_time");
        // C: a NON-correction whose column is also absent → must be ignored (only SchemaCorrections decay here).
        var commonQuery = NewPattern(3, McpPatternType.CommonQuery, McpPatternStatus.Approved);
        commonQuery.ColumnName = "ghost_col";

        var patterns = new List<McpLearnedPattern> { absentColumnCorrection, presentColumnCorrection, commonQuery };

        // Current metadata for public.loans has created_time but NOT created_at.
        var dbMeta = new DatabaseMetadata
        {
            Id = 100,
            DataSourceId = DataSourceId,
            SchemaName = "public",
            TableName = "loans"
        };
        var columns = new List<ColumnMetadata>
        {
            new()
            {
                Id = 1000,
                DatabaseMetadataId = 100,
                DatabaseMetadata = dbMeta,
                ColumnName = "created_time",
                DataType = "timestamptz"
            }
        };

        var service = BuildAggregationService(patterns, columns, out var context);

        await service.DetectStalePatternsAsync(context, ProjectId, CancellationToken.None);

        absentColumnCorrection.SupersededAt.Should().NotBeNull("its referenced column no longer exists");
        absentColumnCorrection.SupersededAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTime.UtcNow);
        presentColumnCorrection.SupersededAt.Should().BeNull("its referenced column still exists");
        commonQuery.SupersededAt.Should().BeNull("only schema corrections are decayed by column existence");

        // History is retained — the stale row is superseded, never deleted.
        patterns.Should().HaveCount(3);
    }

    // ExampleSql is "SELECT {id}" so returned lessons can be mapped back to their pattern id.
    private static int SqlToId(LearnedPatternInfo info) =>
        int.Parse(info.ExampleSql!["SELECT ".Length..]);

    private static McpLearnedPattern NewPattern(int id, McpPatternType type, McpPatternStatus status) =>
        new()
        {
            Id = id,
            DataSourceId = DataSourceId,
            ProjectId = ProjectId,
            SchemaName = "public",
            TableName = "orders",
            PatternType = type,
            PatternContent = $"lesson {id}",
            ExampleQuestion = $"example question {id}",
            ExampleSql = $"SELECT {id}",
            // Distinct, descending confidence — so any accidental fallback to confidence/overlap ordering
            // would produce a DIFFERENT order than the similarity order the test asserts.
            Confidence = 1.0 - (id * 0.05),
            Status = status
        };

    private static McpLearnedPattern NewCorrection(int id, string table, string column) =>
        new()
        {
            Id = id,
            DataSourceId = DataSourceId,
            ProjectId = ProjectId,
            SchemaName = "public",
            TableName = table,
            ColumnName = column,
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = $"correction {id}",
            Confidence = 0.9,
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

    private static KnowledgeGraphService BuildKnowledgeService(
        IBeaconEmbeddingService embedder,
        List<McpLearnedPattern> patterns,
        List<McpEmbedding> embeddings,
        bool semanticEnabled,
        int exemplarTopK)
    {
        var context = new DecayTestContext(
            patterns: BuildDbSet(patterns).Object,
            embeddings: BuildDbSet(embeddings).Object);

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

    private static McpLearningAggregationService BuildAggregationService(
        List<McpLearnedPattern> patterns,
        List<ColumnMetadata> columns,
        out BeaconContext context)
    {
        context = new DecayTestContext(
            patterns: BuildDbSet(patterns).Object,
            columns: BuildDbSet(columns).Object);
        var boundContext = context;

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(boundContext);

        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        return new McpLearningAggregationService(
            factory.Object,
            settings.Object,
            NullLogger<McpLearningAggregationService>.Instance);
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
    /// A <see cref="BeaconContext"/> whose learned-pattern / embedding / column-metadata sets resolve to the
    /// supplied mocked sets, and whose <c>Database.ProviderName</c> reports a NON-Npgsql provider so the
    /// deterministic in-memory cosine path is exercised. SaveChanges is a no-op — the staleness pass mutates
    /// tracked entities in place and its unit of work is owned by the caller.
    /// </summary>
    private sealed class DecayTestContext : BeaconContext
    {
        private static readonly DbContextOptions<DecayTestContext> Options =
            new DbContextOptionsBuilder<DecayTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpLearnedPattern>? _patterns;
        private readonly DbSet<McpEmbedding>? _embeddings;
        private readonly DbSet<ColumnMetadata>? _columns;
        private DatabaseFacade? _database;

        public DecayTestContext(
            DbSet<McpLearnedPattern>? patterns = null,
            DbSet<McpEmbedding>? embeddings = null,
            DbSet<ColumnMetadata>? columns = null) : base(Options, "beacon")
        {
            _patterns = patterns;
            _embeddings = embeddings;
            _columns = columns;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpLearnedPattern) && _patterns != null)
            {
                return (DbSet<TEntity>)(object)_patterns;
            }

            if (typeof(TEntity) == typeof(McpEmbedding) && _embeddings != null)
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            if (typeof(TEntity) == typeof(ColumnMetadata) && _columns != null)
            {
                return (DbSet<TEntity>)(object)_columns;
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
}
