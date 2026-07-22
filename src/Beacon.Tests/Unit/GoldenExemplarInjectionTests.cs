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
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Part A coverage for golden-exemplar injection in
/// <see cref="KnowledgeGraphService.BuildGoldenExemplarBlockAsync"/> plus its placement ABOVE the mined
/// learned-pattern block in <see cref="KnowledgeGraphService.GetSmartContextForAskAsync"/>. Exercises the
/// real logic against a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no DB,
/// no forbidden <c>UseInMemoryDatabase</c> — §4.7). The raw pgvector path needs a live DB, so the context
/// spoofs a non-Npgsql provider and the deterministic in-memory cosine path runs with
/// <see cref="FakeEmbeddingService"/>. Golden cases are data-source-scoped (no project resolution).
/// </summary>
[TestFixture]
public class GoldenExemplarInjectionTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;
    private const string Question = "How many orders were refunded last month?";

    [Test]
    public async Task BuildGoldenExemplarBlock_InjectsTopKActiveCasesOrderedBySimilarity_InactiveFiltered()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // Four cases. #4 is INACTIVE and stores the query-identical vector (cosine 1.0 → the single nearest
        // hit), so if the IsActive filter were missing it would lead the block. The three active cases carry
        // distinct gold SQL so their rendering is unambiguous.
        var vectorById = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("refunded orders count", CancellationToken.None),
            [2] = await embedder.EmbedAsync("revenue by region", CancellationToken.None),
            [3] = await embedder.EmbedAsync("active customers list", CancellationToken.None),
            [4] = queryVector
        };

        const int topK = 5; // ≥ case count so every embedding is a hit and the IsActive filter is the only gate.

        var expectedActiveOrder = vectorById
            .Select(x => (Id: x.Key, Score: EmbeddingCodec.Cosine(queryVector, x.Value)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Id)
            .Where(id => id != 4)
            .ToList();

        var cases = new List<McpEvalCase>
        {
            NewCase(1, Question, "SELECT count(*) FROM orders WHERE status = 4", isActive: true),
            NewCase(2, "revenue by region", "SELECT region, sum(amount) FROM orders GROUP BY region", isActive: true),
            NewCase(3, "active customers", "SELECT count(*) FROM customers WHERE active", isActive: true),
            NewCase(4, "top secret", "SELECT secret FROM vault", isActive: false)
        };

        var embeddings = vectorById
            .Select(x => NewGoldenEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildService(embedder, cases, embeddings, new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = topK,
            GoldenExemplarBudgetChars = 4000
        });

        var block = await service.BuildGoldenExemplarBlockAsync(
            DataSourceId, Question,
            new McpSettingsData
            {
                EnableGoldenExemplars = true,
                EnableSemanticRetrieval = true,
                GoldenExemplarTopK = topK,
                GoldenExemplarBudgetChars = 4000
            },
            CancellationToken.None);

        block.Should().Contain("## Verified query examples (authoritative)");

        // The inactive case is the nearest hit but MUST be filtered out (its embedding maps to no active row).
        block.Should().NotContain("top secret");
        block.Should().NotContain("SELECT secret FROM vault");

        // All three active gold SQLs are injected.
        block.Should().Contain("SELECT count(*) FROM orders WHERE status = 4");
        block.Should().Contain("SELECT region, sum(amount) FROM orders GROUP BY region");
        block.Should().Contain("SELECT count(*) FROM customers WHERE active");

        // Rendered case order == independently-computed similarity order (active-only).
        var renderedOrder = RenderedCaseIds(block);
        renderedOrder.Should().Equal(expectedActiveOrder,
            "the block renders the nearest active golden cases in descending-similarity order");
    }

    [Test]
    public async Task BuildGoldenExemplarBlock_WhenDisabled_InjectsNothing()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        var cases = new List<McpEvalCase> { NewCase(1, Question, "SELECT 1", isActive: true) };
        var embeddings = new List<McpEmbedding> { NewGoldenEmbedding(1, queryVector) };

        // EnableGoldenExemplars = false → the whole feature is off, block is empty even with an active near hit.
        var settings = new McpSettingsData
        {
            EnableGoldenExemplars = false,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5
        };
        var service = BuildService(embedder, cases, embeddings, settings);

        var block = await service.BuildGoldenExemplarBlockAsync(DataSourceId, Question, settings, CancellationToken.None);

        block.Should().BeEmpty();
    }

    [Test]
    public async Task BuildGoldenExemplarBlock_WhenEmbedderUnavailable_InjectsNothing()
    {
        var cases = new List<McpEvalCase> { NewCase(1, Question, "SELECT 1", isActive: true) };
        var embeddings = new List<McpEmbedding> { NewGoldenEmbedding(1, new float[384]) };

        var settings = new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5
        };
        var service = BuildService(new UnavailableEmbeddingService(), cases, embeddings, settings);

        var block = await service.BuildGoldenExemplarBlockAsync(DataSourceId, Question, settings, CancellationToken.None);

        block.Should().BeEmpty();
    }

    [Test]
    public async Task BuildGoldenExemplarBlock_TruncatesToBudget()
    {
        var embedder = new FakeEmbeddingService();

        // Three active cases with large gold SQL; a tight budget must trim the block to the first fitting
        // entries while always emitting at least the header + one example.
        var bigSql = "SELECT " + string.Join(", ", Enumerable.Range(0, 40).Select(i => $"col_{i}")) + " FROM orders";
        var cases = new List<McpEvalCase>
        {
            NewCase(1, "q1", bigSql, isActive: true),
            NewCase(2, "q2", bigSql, isActive: true),
            NewCase(3, "q3", bigSql, isActive: true)
        };
        var embeddings = new List<McpEmbedding>
        {
            NewGoldenEmbedding(1, await embedder.EmbedAsync("q1", CancellationToken.None)),
            NewGoldenEmbedding(2, await embedder.EmbedAsync("q2", CancellationToken.None)),
            NewGoldenEmbedding(3, await embedder.EmbedAsync("q3", CancellationToken.None))
        };

        var budget = bigSql.Length + 100; // room for ~one entry, not three
        var settings = new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5,
            GoldenExemplarBudgetChars = budget
        };
        var service = BuildService(embedder, cases, embeddings, settings);

        var block = await service.BuildGoldenExemplarBlockAsync(DataSourceId, Question, settings, CancellationToken.None);

        block.Should().Contain("## Verified query examples (authoritative)", "at least the header + one example is emitted");
        RenderedCaseIds(block).Count.Should().BeLessThan(3, "the budget must trim later entries");
        // The header line is small; the block stays within budget plus the single always-emitted first entry.
        block.Length.Should().BeLessThan(budget + bigSql.Length + 200);
    }

    [Test]
    public async Task GetSmartContext_PlacesGoldenBlockAboveLearnedPatterns()
    {
        var embedder = new FakeEmbeddingService();

        var dataSource = new DataSource
        {
            Id = DataSourceId,
            Name = "Sales DB",
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "unused-in-test"
        };

        var tables = new List<DatabaseMetadata>
        {
            new()
            {
                Id = 10,
                DataSourceId = DataSourceId,
                SchemaName = "public",
                TableName = "orders",
                TableDescription = "orders",
                Columns = new List<ColumnMetadata>
                {
                    new() { Id = 101, ColumnName = "status", DataType = "int" }
                }
            }
        };

        // One approved learned pattern (renders the "## Learned Patterns" block) + one golden case (renders the
        // "## Verified query examples" block). Both carry an embedding so both retrieval arms return a hit.
        var patterns = new List<McpLearnedPattern>
        {
            new()
            {
                Id = 800,
                DataSourceId = DataSourceId,
                ProjectId = ProjectId,
                SchemaName = "public",
                TableName = "orders",
                PatternType = McpPatternType.CommonQuery,
                PatternContent = "orders refund pattern",
                Status = McpPatternStatus.Approved,
                ExampleQuestion = "orders refunded",
                ExampleSql = "SELECT count(*) FROM orders WHERE status = 4",
                Confidence = 0.9
            }
        };

        var cases = new List<McpEvalCase>
        {
            NewCase(700, Question, "SELECT count(*) FROM orders WHERE status = 4", isActive: true)
        };

        var embeddings = new List<McpEmbedding>
        {
            NewEmbedding(900, McpEmbeddingOwnerType.Exemplar, 800,
                await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask("orders refunded"), CancellationToken.None)),
            NewGoldenEmbedding(700, await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None))
        };

        var settings = new McpSettingsData
        {
            EnableGoldenExemplars = true,
            EnableLearning = true,
            EnableSemanticRetrieval = true,
            GoldenExemplarTopK = 5,
            ExemplarTopK = 5,
            GlossaryTopK = 5
        };

        var context = new SmartContextTestContext(dataSource, tables, patterns, cases, embeddings);
        var service = BuildServiceForContext(embedder, context, settings);

        var result = await service.GetSmartContextForAskAsync(DataSourceId, Question, CancellationToken.None);
        var full = result.FullContext;

        var goldenIdx = full.IndexOf("## Verified query examples (authoritative)", StringComparison.Ordinal);
        var patternsIdx = full.IndexOf("## Learned Patterns", StringComparison.Ordinal);

        goldenIdx.Should().BeGreaterThan(-1, "the golden block must be injected");
        patternsIdx.Should().BeGreaterThan(-1, "the learned-patterns block must be injected");
        goldenIdx.Should().BeLessThan(patternsIdx,
            "human-verified golden examples must rank ABOVE machine-mined patterns");
    }

    private static List<int> RenderedCaseIds(string block)
    {
        // Map the rendered gold SQL back to its seeded case id via the SQL text to assert similarity order.
        var sqlToId = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["SELECT count(*) FROM orders WHERE status = 4"] = 1,
            ["SELECT region, sum(amount) FROM orders GROUP BY region"] = 2,
            ["SELECT count(*) FROM customers WHERE active"] = 3
        };

        return block
            .Split('\n')
            .Select(x => x.Trim())
            .Where(x => sqlToId.ContainsKey(x))
            .Select(x => sqlToId[x])
            .ToList();
    }

    private static McpEvalCase NewCase(int id, string question, string goldSql, bool isActive) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Question = question,
            GoldSql = goldSql,
            IsActive = isActive
        };

    private static McpEmbedding NewGoldenEmbedding(int ownerId, float[] vector) =>
        NewEmbedding(500 + ownerId, McpEmbeddingOwnerType.GoldenCase, ownerId, vector);

    private static McpEmbedding NewEmbedding(int id, McpEmbeddingOwnerType ownerType, int ownerId, float[] vector) =>
        new()
        {
            Id = id,
            DataSourceId = DataSourceId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };

    private static KnowledgeGraphService BuildService(
        IBeaconEmbeddingService embedder,
        List<McpEvalCase> cases,
        List<McpEmbedding> embeddings,
        McpSettingsData settings)
    {
        var context = new GoldenBlockContext(BuildDbSet(embeddings).Object, BuildDbSet(cases).Object);
        return BuildServiceForContext(embedder, context, settings);
    }

    private static KnowledgeGraphService BuildServiceForContext(
        IBeaconEmbeddingService embedder, BeaconContext context, McpSettingsData settings)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var settingsProvider = new Mock<IMcpSettingsProvider>();
        settingsProvider
            .Setup(x => x.GetSettingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(settings);

        return new KnowledgeGraphService(
            factory.Object,
            settingsProvider.Object,
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

    /// <summary>Context exposing only the embedding + golden-case sets for direct block-builder tests.</summary>
    private sealed class GoldenBlockContext : BeaconContext
    {
        private static readonly DbContextOptions<GoldenBlockContext> Options =
            new DbContextOptionsBuilder<GoldenBlockContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpEmbedding> _embeddings;
        private readonly DbSet<McpEvalCase> _cases;
        private DatabaseFacade? _database;

        public GoldenBlockContext(DbSet<McpEmbedding> embeddings, DbSet<McpEvalCase> cases) : base(Options, "beacon")
        {
            _embeddings = embeddings;
            _cases = cases;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpEmbedding))
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            if (typeof(TEntity) == typeof(McpEvalCase))
            {
                return (DbSet<TEntity>)(object)_cases;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>Full context for the fast-path ordering test: data source, metadata, patterns, golden cases,
    /// embeddings. No project links / glossary terms → the glossary block is empty.</summary>
    private sealed class SmartContextTestContext : BeaconContext
    {
        private static readonly DbContextOptions<SmartContextTestContext> Options =
            new DbContextOptionsBuilder<SmartContextTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<DataSource> _dataSources;
        private readonly DbSet<DatabaseMetadata> _metadata;
        private readonly DbSet<McpLearnedPattern> _patterns;
        private readonly DbSet<McpEvalCase> _cases;
        private readonly DbSet<McpEmbedding> _embeddings;
        private readonly DbSet<ProjectDataSource> _links;
        private readonly DbSet<McpGlossaryTerm> _terms;
        private DatabaseFacade? _database;

        public SmartContextTestContext(
            DataSource dataSource,
            List<DatabaseMetadata> metadata,
            List<McpLearnedPattern> patterns,
            List<McpEvalCase> cases,
            List<McpEmbedding> embeddings) : base(Options, "beacon")
        {
            _dataSources = BuildDbSet(new List<DataSource> { dataSource }).Object;
            _metadata = BuildDbSet(metadata).Object;
            _patterns = BuildDbSet(patterns).Object;
            _cases = BuildDbSet(cases).Object;
            _embeddings = BuildDbSet(embeddings).Object;
            _links = BuildDbSet(new List<ProjectDataSource>()).Object;
            _terms = BuildDbSet(new List<McpGlossaryTerm>()).Object;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(DataSource)) return (DbSet<TEntity>)(object)_dataSources;
            if (typeof(TEntity) == typeof(DatabaseMetadata)) return (DbSet<TEntity>)(object)_metadata;
            if (typeof(TEntity) == typeof(McpLearnedPattern)) return (DbSet<TEntity>)(object)_patterns;
            if (typeof(TEntity) == typeof(McpEvalCase)) return (DbSet<TEntity>)(object)_cases;
            if (typeof(TEntity) == typeof(McpEmbedding)) return (DbSet<TEntity>)(object)_embeddings;
            if (typeof(TEntity) == typeof(ProjectDataSource)) return (DbSet<TEntity>)(object)_links;
            if (typeof(TEntity) == typeof(McpGlossaryTerm)) return (DbSet<TEntity>)(object)_terms;
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
