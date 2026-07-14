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
/// Unit coverage for the B4 embedding indexing job. Exercises the real upsert logic against a
/// mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no DB, no forbidden
/// <c>UseInMemoryDatabase</c> — §4.7), asserting the produced <see cref="McpEmbedding"/> rows
/// (count + owner types), the idempotent update-in-place path, and the no-op-when-unavailable
/// short-circuit. The deterministic embedding-text contract is locked by direct helper tests.
/// </summary>
[TestFixture]
public class EmbeddingIndexingServiceTests
{
    private const int ExpectedByteLength = 384 * sizeof(float);

    [Test]
    public void BuildTableText_FormatsSchemaTableAndDescription()
    {
        EmbeddingIndexingService.BuildTableText("public", "orders", "customer orders")
            .Should().Be("public.orders: customer orders");
    }

    [Test]
    public void BuildTableText_OmitsColonWhenDescriptionBlank()
    {
        EmbeddingIndexingService.BuildTableText("public", "orders", null)
            .Should().Be("public.orders");
        EmbeddingIndexingService.BuildTableText("public", "orders", "   ")
            .Should().Be("public.orders");
    }

    [Test]
    public void BuildColumnText_FormatsTableColumnAndDescription()
    {
        EmbeddingIndexingService.BuildColumnText("orders", "total", "order total in cents")
            .Should().Be("orders.total: order total in cents");
        EmbeddingIndexingService.BuildColumnText("orders", "id", null)
            .Should().Be("orders.id");
    }

    [Test]
    public async Task ReindexDataSourceAsync_EmbedsTablesColumnsAndValidatedExemplars()
    {
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = 1,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>
                {
                    new() { Id = 101, ColumnName = "id", DataType = "int", Description = "pk" },
                    new() { Id = 102, ColumnName = "total", DataType = "numeric" }
                }
            },
            new()
            {
                Id = 20,
                DataSourceId = 1,
                SchemaName = "public",
                TableName = "customers",
                Columns = new List<ColumnMetadata>
                {
                    new() { Id = 201, ColumnName = "name", DataType = "text", Description = "full name" }
                }
            }
        };

        // Retrieval-time selection now spans ALL lesson types (§ Architecture ⑧): every valid
        // approved/auto-approved pattern is embedded as an Exemplar, question OR (fallback) content.
        var patterns = new List<McpLearnedPattern>
        {
            NewPattern(500, McpPatternType.CommonQuery, McpPatternStatus.Approved, "How many orders in 2023?"),
            NewPattern(501, McpPatternType.CommonQuery, McpPatternStatus.AutoApproved, "top 5 customers"),
            // Excluded: not approved
            NewPattern(502, McpPatternType.CommonQuery, McpPatternStatus.Pending, "pending question"),
            NewPattern(503, McpPatternType.CommonQuery, McpPatternStatus.Rejected, "rejected question"),
            // Included now: non-CommonQuery type is embedded too (was excluded pre-T2-B4)
            NewPattern(504, McpPatternType.JoinPattern, McpPatternStatus.Approved, "join question"),
            // Included now: no ExampleQuestion → PatternContent is embedded instead (was excluded pre-T2-B4)
            NewPattern(505, McpPatternType.SchemaCorrection, McpPatternStatus.Approved, null),
            // Excluded: superseded (stale) lessons are never indexed
            NewStalePattern(506, McpPatternType.CommonQuery, McpPatternStatus.Approved, "stale question")
        };

        var captured = new List<McpEmbedding>();
        var service = BuildService(tables, patterns, existingEmbeddings: [], captured, removed: [], out _);

        await service.ReindexDataSourceAsync(1, CancellationToken.None);

        captured.Should().HaveCount(9);
        captured.Count(x => x.OwnerType == McpEmbeddingOwnerType.MetadataTable).Should().Be(2);
        captured.Count(x => x.OwnerType == McpEmbeddingOwnerType.MetadataColumn).Should().Be(3);
        captured.Count(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar).Should().Be(4);

        captured.Where(x => x.OwnerType == McpEmbeddingOwnerType.MetadataTable)
            .Select(x => x.OwnerId).Should().BeEquivalentTo(new[] { 10, 20 });
        captured.Where(x => x.OwnerType == McpEmbeddingOwnerType.MetadataColumn)
            .Select(x => x.OwnerId).Should().BeEquivalentTo(new[] { 101, 102, 201 });
        captured.Where(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar)
            .Select(x => x.OwnerId).Should().BeEquivalentTo(new[] { 500, 501, 504, 505 });

        captured.Should().OnlyContain(x =>
            x.DataSourceId == 1
            && x.Dimensions == 384
            && x.EmbeddingVersion == 1
            && x.Model == "bge-small-en-v1.5"
            && x.EmbeddingBytes.Length == ExpectedByteLength);
    }

    [Test]
    public async Task ReindexDataSourceAsync_UpdatesExistingEmbeddingInPlace_DoesNotDuplicate()
    {
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = 1,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>()
            }
        };

        var existingRow = new McpEmbedding
        {
            Id = 99,
            DataSourceId = 1,
            OwnerType = McpEmbeddingOwnerType.MetadataTable,
            OwnerId = 10,
            EmbeddingBytes = [1, 2, 3, 4],
            Model = "stale-model",
            Dimensions = 1,
            EmbeddingVersion = 0
        };

        var captured = new List<McpEmbedding>();
        var service = BuildService(tables, patterns: [], existingEmbeddings: [existingRow], captured, removed: [], out _);

        await service.ReindexDataSourceAsync(1, CancellationToken.None);

        // The single table already had an embedding — it must be updated, never re-added.
        captured.Should().BeEmpty();
        existingRow.EmbeddingBytes.Should().HaveCount(ExpectedByteLength);
        existingRow.Model.Should().Be("bge-small-en-v1.5");
        existingRow.Dimensions.Should().Be(384);
        existingRow.EmbeddingVersion.Should().Be(1);
    }

    [Test]
    public async Task ReindexDataSourceAsync_PrunesStaleExemplarVector_RetainsValidExemplar()
    {
        // A metadata table keeps the input set non-empty so the normal upsert path runs alongside the prune.
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = 1,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>()
            }
        };

        // Only pattern 500 is currently valid (Approved, not superseded); it is the single valid exemplar.
        var patterns = new List<McpLearnedPattern>
        {
            NewPattern(500, McpPatternType.CommonQuery, McpPatternStatus.Approved, "still valid question")
        };

        // Two exemplar vectors linger from a prior index: one for the still-valid pattern (500) and one for a
        // pattern that has since been superseded/rejected/reverted (999, no longer in the valid set).
        var validExemplarRow = new McpEmbedding
        {
            Id = 71,
            DataSourceId = 1,
            OwnerType = McpEmbeddingOwnerType.Exemplar,
            OwnerId = 500,
            EmbeddingBytes = [1, 2, 3, 4],
            Model = "stale-model",
            Dimensions = 1,
            EmbeddingVersion = 0
        };
        var staleExemplarRow = new McpEmbedding
        {
            Id = 72,
            DataSourceId = 1,
            OwnerType = McpEmbeddingOwnerType.Exemplar,
            OwnerId = 999,
            EmbeddingBytes = [9, 9, 9, 9],
            Model = "stale-model",
            Dimensions = 1,
            EmbeddingVersion = 0
        };

        var captured = new List<McpEmbedding>();
        var removed = new List<McpEmbedding>();
        var service = BuildService(
            tables, patterns, existingEmbeddings: [validExemplarRow, staleExemplarRow], captured, removed, out _);

        await service.ReindexDataSourceAsync(1, CancellationToken.None);

        // The orphaned exemplar vector (OwnerId 999) is pruned; the still-valid one (OwnerId 500) is not.
        removed.Should().ContainSingle();
        removed[0].OwnerId.Should().Be(999);
        removed.Should().NotContain(x => x.OwnerId == 500);

        // The valid exemplar is refreshed in place (re-embedded), never re-added and never pruned.
        captured.Should().NotContain(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar);
        validExemplarRow.Model.Should().Be("bge-small-en-v1.5");
        validExemplarRow.EmbeddingBytes.Should().HaveCount(ExpectedByteLength);
    }

    [Test]
    public async Task ReindexDataSourceAsync_PrunesVectorsForDroppedMetadataTableAndColumn()
    {
        // Current metadata: table 10 with a single column 101. Table 20 and column 102 were dropped since the
        // last index — their lingering vectors must be pruned (not just exemplars) so they can't occupy top-k
        // NN slots for a table/column that no longer exists.
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = 1,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>
                {
                    new() { Id = 101, ColumnName = "id", DataType = "int" }
                }
            }
        };

        var validTableRow = StaleVector(60, McpEmbeddingOwnerType.MetadataTable, 10);
        var validColumnRow = StaleVector(61, McpEmbeddingOwnerType.MetadataColumn, 101);
        var droppedTableRow = StaleVector(62, McpEmbeddingOwnerType.MetadataTable, 20);
        var droppedColumnRow = StaleVector(63, McpEmbeddingOwnerType.MetadataColumn, 102);

        var captured = new List<McpEmbedding>();
        var removed = new List<McpEmbedding>();
        var service = BuildService(
            tables,
            patterns: [],
            existingEmbeddings: [validTableRow, validColumnRow, droppedTableRow, droppedColumnRow],
            captured,
            removed,
            out _);

        await service.ReindexDataSourceAsync(1, CancellationToken.None);

        removed.Select(x => x.OwnerId).Should().BeEquivalentTo(new[] { 20, 102 });
        removed.Should().NotContain(x => x.OwnerId == 10 || x.OwnerId == 101);
    }

    [Test]
    public async Task ReindexDataSourceAsync_HandsPersistedRowsToPgVectorColumnWriter()
    {
        // The DB-managed pgvector column is invisible to EF, so the refreshed vector for an already-persisted
        // row must be handed to the vector-column writer keyed by that row's id — otherwise the column the
        // PostgreSQL nearest-neighbor query orders by stays NULL and semantic search returns arbitrary rows.
        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = 1,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>()
            }
        };

        var existingRow = StaleVector(77, McpEmbeddingOwnerType.MetadataTable, 10);

        var writer = new FakeVectorColumnWriter();
        var service = BuildService(
            tables, patterns: [], existingEmbeddings: [existingRow], captured: [], removed: [], out _, writer);

        await service.ReindexDataSourceAsync(1, CancellationToken.None);

        writer.Writes.Should().ContainSingle();
        writer.Writes[0].Id.Should().Be(77);
        writer.Writes[0].Vector.Should().HaveCount(384);
    }

    [Test]
    public async Task ReindexAsync_WhenEmbedderUnavailable_IsNoOpAndTouchesNoDatabase()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var service = new EmbeddingIndexingService(
            factory.Object,
            new UnavailableEmbeddingService(),
            Mock.Of<IMcpSettingsProvider>(),
            new FakeVectorColumnWriter(),
            NullLogger<EmbeddingIndexingService>.Instance);

        await service.ReindexAsync(CancellationToken.None);

        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    private static McpLearnedPattern NewPattern(int id, McpPatternType type, McpPatternStatus status, string? question) =>
        new()
        {
            Id = id,
            DataSourceId = 1,
            ProjectId = 1,
            SchemaName = "public",
            TableName = "orders",
            PatternType = type,
            PatternContent = "content",
            Status = status,
            ExampleQuestion = question,
            ExampleSql = question == null ? null : "SELECT 1"
        };

    private static McpLearnedPattern NewStalePattern(int id, McpPatternType type, McpPatternStatus status, string? question)
    {
        var pattern = NewPattern(id, type, status, question);
        pattern.SupersededAt = DateTime.UtcNow;
        return pattern;
    }

    // A lingering embedding row from a prior index (stale model/dimensions) used to assert prune + write paths.
    private static McpEmbedding StaleVector(int id, McpEmbeddingOwnerType ownerType, int ownerId) =>
        new()
        {
            Id = id,
            DataSourceId = 1,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = [1, 2, 3, 4],
            Model = "stale-model",
            Dimensions = 1,
            EmbeddingVersion = 0
        };

    private static EmbeddingIndexingService BuildService(
        List<DatabaseMetadata> tables,
        List<McpLearnedPattern> patterns,
        List<McpEmbedding> existingEmbeddings,
        List<McpEmbedding> captured,
        List<McpEmbedding> removed,
        out Mock<IDbContextFactory<BeaconContext>> factory,
        FakeVectorColumnWriter? vectorWriter = null)
    {
        var metadataSet = BuildDbSet(tables);
        var patternSet = BuildDbSet(patterns);
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

        var context = new IndexingTestContext(metadataSet.Object, patternSet.Object, embeddingSet.Object);

        factory = new Mock<IDbContextFactory<BeaconContext>>();
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
            vectorWriter ?? new FakeVectorColumnWriter(),
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
    /// A <see cref="BeaconContext"/> whose metadata / learned-pattern / embedding sets resolve to the
    /// supplied mocked sets. SaveChanges is short-circuited — writes are observed through the mocked
    /// embedding set (AddRange capture) and in-place mutation, so no DB connection is ever opened.
    /// </summary>
    private sealed class IndexingTestContext : BeaconContext
    {
        private static readonly DbContextOptions<IndexingTestContext> Options =
            new DbContextOptionsBuilder<IndexingTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<DatabaseMetadata> _metadata;
        private readonly DbSet<McpLearnedPattern> _patterns;
        private readonly DbSet<McpEmbedding> _embeddings;

        public IndexingTestContext(
            DbSet<DatabaseMetadata> metadata,
            DbSet<McpLearnedPattern> patterns,
            DbSet<McpEmbedding> embeddings) : base(Options, "beacon")
        {
            _metadata = metadata;
            _patterns = patterns;
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
