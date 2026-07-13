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
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpGlossary;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Tier-3 ⑪ coverage for the business-glossary injection in
/// <see cref="KnowledgeGraphService.BuildGlossaryBlockAsync"/> plus the CRUD handlers. Exercises the real
/// injection logic against a mocked <see cref="BeaconContext"/> backed by the async-queryable doubles (no
/// DB, no forbidden <c>UseInMemoryDatabase</c> — §4.7). The PostgreSQL raw <c>&lt;=&gt;</c> path needs a
/// live pgvector DB, so the context spoofs a non-Npgsql provider and the deterministic in-memory cosine
/// path runs with <see cref="FakeEmbeddingService"/>.
/// </summary>
[TestFixture]
public class GlossaryInjectionTests
{
    private const int DataSourceId = 7;
    private const int ProjectId = 3;
    private const string Question = "What is our total revenue this year?";

    [Test]
    public async Task BuildGlossaryBlock_InjectsTopKActiveTermsOrderedBySimilarity_InactiveFiltered()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        // Four terms. #4 is INACTIVE and stores the query-identical vector (cosine 1.0 → the single nearest
        // hit), so if the IsActive filter were missing it would lead the block. The three active terms carry
        // distinct definitions/targets so their rendering is unambiguous.
        var vectorById = new Dictionary<int, float[]>
        {
            [1] = await embedder.EmbedAsync("gross sales money earned", CancellationToken.None),
            [2] = await embedder.EmbedAsync("people signing in recently", CancellationToken.None),
            [3] = await embedder.EmbedAsync("customers leaving cancellations", CancellationToken.None),
            [4] = queryVector
        };

        const int topK = 5; // ≥ term count so every embedding is a hit and the IsActive filter is the only gate.

        // Independently compute the NN order the in-memory path must produce, then drop the inactive #4 —
        // that is the exact term order the rendered block must follow.
        var expectedActiveOrder = vectorById
            .Select(x => (Id: x.Key, Score: EmbeddingCodec.Cosine(queryVector, x.Value)))
            .OrderByDescending(x => x.Score)
            .Select(x => x.Id)
            .Where(id => id != 4)
            .ToList();

        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true, metric: "SUM(amount)"),
            NewTerm(2, "active user", "A user with a login in the last 30 days", isActive: true,
                schema: "analytics", table: "users", column: "last_login"),
            NewTerm(3, "churn", "Customers who canceled their subscription", isActive: true),
            NewTerm(4, "top secret", "SHOULD NOT APPEAR IN THE BLOCK", isActive: false)
        };

        var embeddings = vectorById
            .Select(x => NewGlossaryEmbedding(x.Key, x.Value))
            .ToList();

        var service = BuildService(embedder, terms, embeddings, semanticEnabled: true, glossaryTopK: topK);

        var block = await service.BuildGlossaryBlockAsync(
            DataSourceId, Question, new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = topK },
            CancellationToken.None);

        block.Should().Contain("## Business Glossary");

        // The inactive term is the nearest hit but MUST be filtered out (its embedding maps to no active row).
        block.Should().NotContain("SHOULD NOT APPEAR IN THE BLOCK");
        block.Should().NotContain("top secret");

        // All three active definitions are injected, with their target column / metric rendered.
        block.Should().Contain("Total sales amount recognized");
        block.Should().Contain("`SUM(amount)`");
        block.Should().Contain("A user with a login in the last 30 days");
        block.Should().Contain("analytics.users.last_login");
        block.Should().Contain("Customers who canceled their subscription");

        // Rendered term order == the independently-computed similarity order (active-only).
        var renderedOrder = RenderedTermIds(block);
        renderedOrder.Should().Equal(expectedActiveOrder,
            "the block renders the nearest active glossary terms in descending-similarity order");
    }

    [Test]
    public async Task BuildGlossaryBlock_WhenEmbedderUnavailable_InjectsNothing()
    {
        // Behaviour-preserving fallback: no embedder → no glossary block at all, even with active terms + vectors.
        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true, metric: "SUM(amount)")
        };
        var embeddings = new List<McpEmbedding> { NewGlossaryEmbedding(1, new float[384]) };

        var service = BuildService(
            new UnavailableEmbeddingService(), terms, embeddings, semanticEnabled: true, glossaryTopK: 5);

        var block = await service.BuildGlossaryBlockAsync(
            DataSourceId, Question, new McpSettingsData { EnableSemanticRetrieval = true, GlossaryTopK = 5 },
            CancellationToken.None);

        block.Should().BeEmpty();
    }

    [Test]
    public async Task BuildGlossaryBlock_WhenSemanticRetrievalDisabled_InjectsNothing()
    {
        var embedder = new FakeEmbeddingService();
        var queryVector = await embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(Question), CancellationToken.None);

        var terms = new List<McpGlossaryTerm>
        {
            NewTerm(1, "revenue", "Total sales amount recognized", isActive: true)
        };
        var embeddings = new List<McpEmbedding> { NewGlossaryEmbedding(1, queryVector) };

        var service = BuildService(embedder, terms, embeddings, semanticEnabled: false, glossaryTopK: 5);

        var block = await service.BuildGlossaryBlockAsync(
            DataSourceId, Question, new McpSettingsData { EnableSemanticRetrieval = false, GlossaryTopK = 5 },
            CancellationToken.None);

        block.Should().BeEmpty();
    }

    [Test]
    public async Task CreateGlossaryTermHandler_AddsTermWithSuppliedFields()
    {
        McpGlossaryTerm? added = null;
        var set = BuildDbSet(new List<McpGlossaryTerm>());
        set.Setup(x => x.Add(It.IsAny<McpGlossaryTerm>()))
            .Callback<McpGlossaryTerm>(x => added = x);

        var context = new GlossaryHandlerContext(set.Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var handler = new CreateGlossaryTermHandler(factory.Object);

        await handler.Handle(new CreateGlossaryTermCommand
        {
            ProjectId = ProjectId,
            DataSourceId = DataSourceId,
            Term = "revenue",
            Synonyms = "sales, turnover",
            Definition = "Total sales amount recognized",
            MetricExpression = "SUM(amount)"
        }, CancellationToken.None);

        added.Should().NotBeNull();
        added!.ProjectId.Should().Be(ProjectId);
        added.DataSourceId.Should().Be(DataSourceId);
        added.Term.Should().Be("revenue");
        added.Synonyms.Should().Be("sales, turnover");
        added.Definition.Should().Be("Total sales amount recognized");
        added.MetricExpression.Should().Be("SUM(amount)");
        added.IsActive.Should().BeTrue("new terms are active on creation");
    }

    [Test]
    public async Task CreateGlossaryTermHandler_BlankTerm_Throws()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);
        var handler = new CreateGlossaryTermHandler(factory.Object);

        var act = async () => await handler.Handle(new CreateGlossaryTermCommand
        {
            ProjectId = ProjectId,
            Term = "   ",
            Definition = "something"
        }, CancellationToken.None);

        // The guard runs before any DB access, so the strict factory is never touched.
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*term text is required*");
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_WhenMissing_Throws()
    {
        var context = new GlossaryHandlerContext(BuildDbSet(new List<McpGlossaryTerm>()).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);

        var handler = new UpdateGlossaryTermHandler(factory.Object);

        var act = async () => await handler.Handle(new UpdateGlossaryTermCommand { Id = 404, Term = "x" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*404 not found*");
    }

    [Test]
    public async Task DeleteGlossaryTermHandler_SoftDeactivates_TermRetained()
    {
        var term = NewTerm(1, "revenue", "Total sales amount recognized", isActive: true);
        var handler = new DeleteGlossaryTermHandler(BuildGlossaryHandlerFactory(term));

        await handler.Handle(new DeleteGlossaryTermCommand(1), CancellationToken.None);

        // Soft delete: the flag flips to false but the row itself is retained (governed history).
        term.IsActive.Should().BeFalse();
    }

    [Test]
    public async Task DeleteGlossaryTermHandler_WhenMissing_Throws()
    {
        var handler = new DeleteGlossaryTermHandler(BuildGlossaryHandlerFactory());

        var act = async () => await handler.Handle(new DeleteGlossaryTermCommand(404), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*404 not found*");
    }

    [Test]
    public async Task GetGlossaryTermsHandler_ExcludesInactiveByDefault_IncludesWhenRequested()
    {
        var handler = new GetGlossaryTermsHandler(BuildGlossaryHandlerFactory(
            NewTerm(1, "revenue", "def", isActive: true),
            NewTerm(2, "legacy", "def", isActive: false)));

        var activeOnly = await handler.Handle(
            new GetGlossaryTermsQuery { ProjectId = ProjectId }, CancellationToken.None);
        activeOnly.Select(x => x.Id).Should().Equal(1);

        var all = await handler.Handle(
            new GetGlossaryTermsQuery { ProjectId = ProjectId, IncludeInactive = true }, CancellationToken.None);
        all.Select(x => x.Id).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Test]
    public async Task GetGlossaryTermsHandler_ScopesToDataSource_WhenSupplied()
    {
        var handler = new GetGlossaryTermsHandler(BuildGlossaryHandlerFactory(
            NewTermWithDataSource(1, "revenue", dataSourceId: DataSourceId),
            NewTermWithDataSource(2, "other", dataSourceId: 999)));

        var scoped = await handler.Handle(
            new GetGlossaryTermsQuery { ProjectId = ProjectId, DataSourceId = DataSourceId }, CancellationToken.None);

        scoped.Select(x => x.Id).Should().Equal(1);
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_AppliesSuppliedFields_LeavesOthersUnchanged()
    {
        var term = NewTerm(1, "revenue", "Old definition", isActive: true, metric: "SUM(amount)");
        var handler = new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term));

        await handler.Handle(new UpdateGlossaryTermCommand { Id = 1, Definition = "New definition" }, CancellationToken.None);

        term.Definition.Should().Be("New definition", "the supplied field is applied");
        term.Term.Should().Be("revenue", "unsupplied fields are left unchanged");
        term.MetricExpression.Should().Be("SUM(amount)", "unsupplied fields are left unchanged");
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_BlankTerm_Throws_AndLeavesTermUnchanged()
    {
        var term = NewTerm(1, "revenue", "def", isActive: true);
        var handler = new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term));

        var act = async () => await handler.Handle(new UpdateGlossaryTermCommand { Id = 1, Term = "   " }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*term text cannot be blank*");
        term.Term.Should().Be("revenue", "the guard must not blank the stored term");
    }

    [Test]
    public async Task UpdateGlossaryTermHandler_BlankDefinition_Throws()
    {
        var term = NewTerm(1, "revenue", "def", isActive: true);
        var handler = new UpdateGlossaryTermHandler(BuildGlossaryHandlerFactory(term));

        var act = async () => await handler.Handle(new UpdateGlossaryTermCommand { Id = 1, Definition = " " }, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*definition cannot be blank*");
    }

    private static IDbContextFactory<BeaconContext> BuildGlossaryHandlerFactory(params McpGlossaryTerm[] terms)
    {
        var context = new GlossaryHandlerContext(BuildDbSet(terms.ToList()).Object);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(context);
        return factory.Object;
    }

    private static McpGlossaryTerm NewTermWithDataSource(int id, string term, int dataSourceId) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            DataSourceId = dataSourceId,
            Term = term,
            Definition = "def",
            IsActive = true
        };

    private static List<int> RenderedTermIds(string block)
    {
        // Map the rendered term names back to their seeded ids to assert similarity order.
        var nameToId = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["revenue"] = 1,
            ["active user"] = 2,
            ["churn"] = 3
        };

        return block
            .Split('\n')
            .Where(x => x.StartsWith("- **", StringComparison.Ordinal))
            .Select(x => x["- **".Length..])
            .Select(x => x[..x.IndexOf("**", StringComparison.Ordinal)])
            .Select(x => nameToId[x])
            .ToList();
    }

    private static McpGlossaryTerm NewTerm(
        int id, string term, string definition, bool isActive,
        string? schema = null, string? table = null, string? column = null, string? metric = null) =>
        new()
        {
            Id = id,
            ProjectId = ProjectId,
            Term = term,
            Definition = definition,
            TargetSchema = schema,
            TargetTable = table,
            TargetColumn = column,
            MetricExpression = metric,
            IsActive = isActive
        };

    private static McpEmbedding NewGlossaryEmbedding(int ownerId, float[] vector) =>
        new()
        {
            Id = 500 + ownerId,
            DataSourceId = 0,
            ProjectId = ProjectId,
            OwnerType = McpEmbeddingOwnerType.GlossaryTerm,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };

    private static KnowledgeGraphService BuildService(
        IBeaconEmbeddingService embedder,
        List<McpGlossaryTerm> terms,
        List<McpEmbedding> embeddings,
        bool semanticEnabled,
        int glossaryTopK)
    {
        var links = new List<ProjectDataSource>
        {
            new() { Id = 1, ProjectId = ProjectId, DataSourceId = DataSourceId }
        };

        var context = new GlossaryInjectionContext(
            BuildDbSet(links).Object,
            BuildDbSet(embeddings).Object,
            BuildDbSet(terms).Object);

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
                GlossaryTopK = glossaryTopK
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
    /// A <see cref="BeaconContext"/> whose project-link / embedding / glossary sets resolve to the supplied
    /// mocked sets, and whose <c>Database.ProviderName</c> reports a NON-Npgsql provider so the deterministic
    /// in-memory cosine retrieval path is exercised (the raw pgvector path needs a live DB).
    /// </summary>
    private sealed class GlossaryInjectionContext : BeaconContext
    {
        private static readonly DbContextOptions<GlossaryInjectionContext> Options =
            new DbContextOptionsBuilder<GlossaryInjectionContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<ProjectDataSource> _links;
        private readonly DbSet<McpEmbedding> _embeddings;
        private readonly DbSet<McpGlossaryTerm> _terms;
        private DatabaseFacade? _database;

        public GlossaryInjectionContext(
            DbSet<ProjectDataSource> links, DbSet<McpEmbedding> embeddings, DbSet<McpGlossaryTerm> terms)
            : base(Options, "beacon")
        {
            _links = links;
            _embeddings = embeddings;
            _terms = terms;
        }

        public override DatabaseFacade Database => _database ??= new NonNpgsqlDatabaseFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)_links;
            }

            if (typeof(TEntity) == typeof(McpEmbedding))
            {
                return (DbSet<TEntity>)(object)_embeddings;
            }

            if (typeof(TEntity) == typeof(McpGlossaryTerm))
            {
                return (DbSet<TEntity>)(object)_terms;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    /// <summary>Minimal <see cref="BeaconContext"/> exposing only the glossary set for the CRUD handler tests.</summary>
    private sealed class GlossaryHandlerContext : BeaconContext
    {
        private static readonly DbContextOptions<GlossaryHandlerContext> Options =
            new DbContextOptionsBuilder<GlossaryHandlerContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpGlossaryTerm> _terms;

        public GlossaryHandlerContext(DbSet<McpGlossaryTerm> terms) : base(Options, "beacon") => _terms = terms;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpGlossaryTerm))
            {
                return (DbSet<TEntity>)(object)_terms;
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
