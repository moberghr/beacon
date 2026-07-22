using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part A coverage for golden-case embedding indexing in <see cref="EmbeddingIndexingService"/>. Exercises
/// the real upsert/prune logic against a mocked <see cref="BeaconContext"/> backed by the async-queryable
/// doubles (no DB, no forbidden <c>UseInMemoryDatabase</c> — §4.7): ACTIVE golden cases are embedded as
/// <see cref="McpEmbeddingOwnerType.GoldenCase"/> rows, and a lingering vector for a deactivated/deleted
/// case is pruned so it can never occupy a top-k retrieval slot.
/// </summary>
[TestFixture]
public class EmbeddingIndexingGoldenCaseTests
{
    private const int ExpectedByteLength = 384 * sizeof(float);
    private const int DataSourceId = 1;

    [Test]
    public async Task ReindexDataSourceAsync_EmbedsActiveGoldenCasesAsGoldenCaseType_InactiveExcluded()
    {
        // One metadata table keeps the input set non-empty alongside the golden cases.
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = DataSourceId,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>()
            }
        };

        var goldenCases = new List<McpEvalCase>
        {
            NewCase(700, "How many orders were refunded last month?", isActive: true),
            NewCase(701, "Total revenue by region this year", isActive: true),
            // Excluded: inactive golden case must NOT be embedded.
            NewCase(702, "SHOULD NOT BE EMBEDDED", isActive: false)
        };

        var captured = new List<McpEmbedding>();
        var service = BuildService(tables, goldenCases, existingEmbeddings: [], captured, removed: []);

        await service.ReindexDataSourceAsync(DataSourceId, CancellationToken.None);

        var golden = captured
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.GoldenCase)
            .ToList();

        golden.Select(x => x.OwnerId).Should().BeEquivalentTo(new[] { 700, 701 },
            "only the two ACTIVE golden cases are embedded, keyed by McpEvalCase.Id");
        golden.Should().OnlyContain(x =>
            x.DataSourceId == DataSourceId
            && x.Dimensions == 384
            && x.EmbeddingVersion == 1
            && x.Model == "bge-small-en-v1.5"
            && x.EmbeddingBytes.Length == ExpectedByteLength);
    }

    [Test]
    public async Task ReindexDataSourceAsync_PrunesGoldenCaseVectorForInactiveCase_RetainsActive()
    {
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = DataSourceId,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>()
            }
        };

        // Only case 700 is currently active; case 999 was deactivated/deleted since the last index.
        var goldenCases = new List<McpEvalCase>
        {
            NewCase(700, "still valid question", isActive: true)
        };

        var validGoldenRow = GoldenVector(81, 700);
        var staleGoldenRow = GoldenVector(82, 999);

        var captured = new List<McpEmbedding>();
        var removed = new List<McpEmbedding>();
        var service = BuildService(
            tables, goldenCases, existingEmbeddings: [validGoldenRow, staleGoldenRow], captured, removed);

        await service.ReindexDataSourceAsync(DataSourceId, CancellationToken.None);

        // The orphaned golden vector (OwnerId 999) is pruned; the still-valid one (700) is not.
        removed.Should().ContainSingle();
        removed[0].OwnerId.Should().Be(999);
        removed.Should().NotContain(x => x.OwnerId == 700);

        // The valid golden vector is refreshed in place (re-embedded), never re-added and never pruned.
        captured.Should().NotContain(x => x.OwnerType == McpEmbeddingOwnerType.GoldenCase);
        validGoldenRow.Model.Should().Be("bge-small-en-v1.5");
        validGoldenRow.EmbeddingBytes.Should().HaveCount(ExpectedByteLength);
    }

    private static McpEvalCase NewCase(int id, string question, bool isActive) =>
        new()
        {
            Id = id,
            ProjectId = 3,
            DataSourceId = DataSourceId,
            Question = question,
            GoldSql = "SELECT 1",
            IsActive = isActive
        };

    private static McpEmbedding GoldenVector(int id, int ownerId) =>
        new()
        {
            Id = id,
            DataSourceId = DataSourceId,
            OwnerType = McpEmbeddingOwnerType.GoldenCase,
            OwnerId = ownerId,
            EmbeddingBytes = [1, 2, 3, 4],
            Model = "stale-model",
            Dimensions = 1,
            EmbeddingVersion = 0
        };

    private static EmbeddingIndexingService BuildService(
        List<DatabaseMetadata> tables,
        List<McpEvalCase> goldenCases,
        List<McpEmbedding> existingEmbeddings,
        List<McpEmbedding> captured,
        List<McpEmbedding> removed)
    {
        var metadataSet = BuildDbSet(tables);
        var patternSet = BuildDbSet(new List<McpLearnedPattern>());
        var goldenSet = BuildDbSet(goldenCases);
        var embeddingSet = BuildDbSet(existingEmbeddings);
        embeddingSet
            .Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<McpEmbedding>>(), It.IsAny<CancellationToken>()))
            .Returns((IEnumerable<McpEmbedding> entities, CancellationToken _) =>
            {
                captured.AddRange(entities);
                return Task.CompletedTask;
            });
        embeddingSet
            .Setup(x => x.RemoveRange(It.IsAny<IEnumerable<McpEmbedding>>()))
            .Callback((IEnumerable<McpEmbedding> entities) => removed.AddRange(entities));

        var context = new GoldenIndexingContext(
            metadataSet.Object, patternSet.Object, goldenSet.Object, embeddingSet.Object);

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settings = new Mock<IMcpSettingsProvider>();
        settings
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new McpSettingsData());

        return new EmbeddingIndexingService(
            factory.Object,
            new FakeEmbeddingService(),
            settings.Object,
            new FakeVectorColumnWriter(),
            NullLogger<EmbeddingIndexingService>.Instance);
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
    /// A <see cref="BeaconContext"/> whose metadata / learned-pattern / golden-case / embedding sets resolve
    /// to the supplied mocked sets. SaveChanges is short-circuited — writes are observed through the mocked
    /// embedding set (AddRange capture / RemoveRange), so no DB connection is ever opened.
    /// </summary>
    private sealed class GoldenIndexingContext : BeaconContext
    {
        private static readonly DbContextOptions<GoldenIndexingContext> Options =
            new DbContextOptionsBuilder<GoldenIndexingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<DatabaseMetadata> _metadata;
        private readonly DbSet<McpLearnedPattern> _patterns;
        private readonly DbSet<McpEvalCase> _goldenCases;
        private readonly DbSet<McpEmbedding> _embeddings;

        public GoldenIndexingContext(
            DbSet<DatabaseMetadata> metadata,
            DbSet<McpLearnedPattern> patterns,
            DbSet<McpEvalCase> goldenCases,
            DbSet<McpEmbedding> embeddings) : base(Options, "beacon")
        {
            _metadata = metadata;
            _patterns = patterns;
            _goldenCases = goldenCases;
            _embeddings = embeddings;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DatabaseMetadata))
            {
                return (DbSet<TEntity>)(object)_metadata;
            }

            if (typeof(TEntity) == typeof(McpLearnedPattern))
            {
                return (DbSet<TEntity>)(object)_patterns;
            }

            if (typeof(TEntity) == typeof(McpEvalCase))
            {
                return (DbSet<TEntity>)(object)_goldenCases;
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
}
