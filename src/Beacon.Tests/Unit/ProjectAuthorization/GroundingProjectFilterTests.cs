using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.DbtIntegration;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Grounding reads project material only through the caller's project (§1.12). Two projects link a shared data source and
/// each has its own documentation, documentation chunks, glossary terms and dbt repository; the other project also links
/// a data source of its own. The caller's project search, data-source list, catalog search, documentation chunks and
/// glossary see only the caller's material — also when a vector stamped with the caller's project points at another
/// project's row (a stale or mis-stamped index entry). On PostgreSQL the nearest-neighbour queries carry the project
/// predicate, proven on the captured SQL. Nothing reaches a database (§4.7).
/// </summary>
[TestFixture]
public class GroundingProjectFilterTests
{
    private const int CallerProjectId = 41;
    private const int OtherProjectId = 42;
    private const int SharedDataSourceId = 7;
    private const int OtherProjectsDataSourceId = 8;
    private const int CallerChunkId = 1;
    private const int OtherChunkId = 2;
    private const int CallerTermId = 1;
    private const int OtherTermId = 2;
    private const int OtherDbtRepositoryId = 20;
    private const string ChunkQuestion = "quarterly close";
    private const string GlossaryQuestion = "What was the salary mass?";

    [Test]
    public async Task ProjectSearch_ReadsTheTablesOfTheCallersDataSourcesOnly()
    {
        var service = Service(new UnavailableEmbeddingService());

        var caller = await service.SearchProjectAsync("payroll", CallerProjectId);
        var other = await service.SearchProjectAsync("payroll", OtherProjectId);

        caller.Should().BeEmpty("the payroll table belongs to a data source only the other project links");
        other.Select(x => x.TableName).Should().Contain("payroll");
    }

    [Test]
    public async Task ProjectSearch_DocumentationSectionsComeFromTheCallersProjectOnly()
    {
        var service = Service(new UnavailableEmbeddingService());

        var results = await service.SearchProjectAsync("notes", CallerProjectId);

        var documentation = results
            .Where(x => x.Type == "documentation")
            .ToList();
        documentation.Select(x => x.Description).Should().ContainSingle().Which.Should().Contain("caller notes");
        documentation.Should().OnlyContain(x => x.DataSourceName == "Finance");
    }

    [Test]
    public async Task ProjectSearch_SemanticArm_MapsOnlyTheCallersDocumentationChunks()
    {
        var service = Service(new FakeEmbeddingService());

        var results = await service.SearchProjectAsync(ChunkQuestion, CallerProjectId);

        results.Select(x => x.Description).Should().Contain(x => x!.Contains("caller chunk"));
        results.Select(x => x.Description).Should().NotContain(x => x != null && x.Contains("other chunk"));
    }

    [Test]
    public async Task ProjectDataSources_AreTheOnesTheCallersProjectLinks()
    {
        var service = Service(new UnavailableEmbeddingService());

        var caller = await service.GetProjectDataSourcesAsync(CallerProjectId);
        var other = await service.GetProjectDataSourcesAsync(OtherProjectId);

        caller.Select(x => x.DataSourceId).Should().Equal(SharedDataSourceId);
        other.Select(x => x.DataSourceId).Should().BeEquivalentTo([SharedDataSourceId, OtherProjectsDataSourceId]);
    }

    [Test]
    public async Task CatalogSearch_ReadsTheColumnsOfTheCallersDataSourcesOnly()
    {
        var service = Service(new UnavailableEmbeddingService());

        var caller = await service.SearchAsync("salary", CallerProjectId);
        var other = await service.SearchAsync("salary", OtherProjectId);

        caller.Should().BeEmpty("the salary column belongs to a data source only the other project links");
        other.Select(x => x.ColumnName).Should().Contain("salary");
    }

    [Test]
    public async Task DocumentationChunks_ComeFromTheCallersProjectOnly()
    {
        var service = Service(new FakeEmbeddingService());

        var hits = await service.GetRelevantDocChunksAsync(CallerProjectId, ChunkQuestion, topK: 5);

        hits.Select(x => x.ChunkText).Should().Equal("caller chunk: quarterly close");
    }

    [Test]
    public async Task Glossary_TermsComeFromTheCallersProjectOnly()
    {
        var service = Service(new FakeEmbeddingService());

        var block = await service.BuildGlossaryBlockAsync(CallerProjectId, GlossaryQuestion, new McpSettingsData { EnableSemanticRetrieval = true }, CancellationToken.None);

        block.Should().Contain("Revenue").And.NotContain("Salary mass");
    }

    [Test]
    public async Task OnPostgreSql_TheExemplarVectorQuery_IsFilteredByTheCallersProject()
    {
        var capture = new SqlCapture().ThenNoRows();
        var service = Service(new FakeEmbeddingService(), capture.Factory());

        await service.GetRelevantPatternsAsync(SharedDataSourceId, CallerProjectId, ["public.orders"], "how many orders?");

        capture.Commands.Should().HaveCount(2, "the pattern bank, then the exemplar nearest-neighbour query");
        AssertProjectScopedVectorQuery(capture, 1);
        capture.Commands[1].Should().Contain("data_source_id = ANY(");
    }

    [Test]
    public async Task OnPostgreSql_TheDocumentationChunkVectorQuery_IsFilteredByTheCallersProject()
    {
        var capture = new SqlCapture();
        var service = Service(new FakeEmbeddingService(), capture.Factory());

        await service.GetRelevantDocChunksAsync(CallerProjectId, ChunkQuestion, topK: 3);

        capture.Commands.Should().ContainSingle();
        AssertProjectScopedVectorQuery(capture, 0);
    }

    [Test]
    public async Task OnPostgreSql_TheGlossaryVectorQuery_IsFilteredByTheCallersProject()
    {
        var capture = new SqlCapture();
        var service = Service(new FakeEmbeddingService(), capture.Factory());

        await service.BuildGlossaryBlockAsync(CallerProjectId, GlossaryQuestion, new McpSettingsData { EnableSemanticRetrieval = true }, CancellationToken.None);

        capture.Commands.Should().ContainSingle();
        AssertProjectScopedVectorQuery(capture, 0);
    }

    [Test]
    public async Task DbtImport_UsesTheCallersOwnDbtRepository_NeverAnotherProjects()
    {
        var saved = new List<object>();
        var otherRepository = new GitHubRepository
        {
            Id = OtherDbtRepositoryId,
            ProjectId = OtherProjectId,
            RepositoryUrl = "dbt://imported",
            TotalReferencesFound = 5
        };
        var sets = new Dictionary<Type, object>
        {
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(
                [
                    new ProjectDataSource { ProjectId = CallerProjectId, DataSourceId = SharedDataSourceId },
                    new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = SharedDataSourceId }
                ],
                saved),
            [typeof(GitHubRepository)] = RecordingBeaconContext.MemorySet([otherRepository], saved),
            [typeof(CodeReference)] = RecordingBeaconContext.MemorySet(new List<CodeReference>(), saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(new List<DatabaseMetadata>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));
        var service = new DbtIntegrationService(factory.Object, NullLogger<DbtIntegrationService>.Instance);
        var manifest = new DbtManifest { Models = [new DbtModel("orders", "sales", "Orders", "table", [], [])] };

        await service.ImportModelsAsync(CallerProjectId, SharedDataSourceId, manifest);

        saved.OfType<GitHubRepository>().Should().ContainSingle().Which.ProjectId.Should().Be(CallerProjectId);
        saved.OfType<CodeReference>().Should().ContainSingle().Which.GitHubRepositoryId.Should().NotBe(OtherDbtRepositoryId);
        otherRepository.TotalReferencesFound.Should().Be(5, "the other project's dbt repository is not touched");
    }

    // The vector query keeps the project predicate, and binds the caller's project.
    private static void AssertProjectScopedVectorQuery(SqlCapture capture, int index)
    {
        capture.Commands[index].Should().Contain("FROM mcp_embeddings").And.Contain("project_id = @");
        capture.CommandParameters[index].Values.Should().Contain(CallerProjectId);
    }

    private static KnowledgeGraphService Service(IBeaconEmbeddingService embedder, IDbContextFactory<BeaconContext>? factory = null)
    {
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnableSemanticRetrieval = true });

        return new KnowledgeGraphService(
            factory ?? World(),
            settings.Object,
            SettingsProviderMock.Unshared(settings.Object),
            embedder,
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.None,
            NullLogger<KnowledgeGraphService>.Instance);
    }

    private static IDbContextFactory<BeaconContext> World()
    {
        var shared = DataSource(SharedDataSourceId, "warehouse");
        var otherProjectsSource = DataSource(OtherProjectsDataSourceId, "hr");
        var caller = new Project { Id = CallerProjectId, Name = "Finance" };
        var other = new Project { Id = OtherProjectId, Name = "Payroll" };
        var orders = Table(shared, "public", "orders", "orders placed", "id", "placed_time");
        var payroll = Table(otherProjectsSource, "hr", "payroll", "payroll ledger", "salary");
        var columns = new[] { orders, payroll }
            .SelectMany(x => x.Columns)
            .ToList();
        var embedder = new FakeEmbeddingService();
        var chunkVector = embedder.EmbedAsync(ChunkQuestion, CancellationToken.None).GetAwaiter().GetResult();
        var glossaryVector = embedder.EmbedAsync(EmbeddingMaskingHelper.Mask(GlossaryQuestion), CancellationToken.None).GetAwaiter().GetResult();

        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet([shared, otherProjectsSource], saved),
            [typeof(Project)] = RecordingBeaconContext.MemorySet([caller, other], saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(
                [
                    new ProjectDataSource { ProjectId = CallerProjectId, DataSourceId = SharedDataSourceId },
                    new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = SharedDataSourceId },
                    new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = OtherProjectsDataSourceId }
                ],
                saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet([orders, payroll], saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(columns, saved),
            [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(
                [
                    Section(1, caller, "orders: caller notes"),
                    Section(2, other, "orders: other notes")
                ],
                saved),
            [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet(new List<ProjectDocumentation>(), saved),
            [typeof(ProjectImportedDocument)] = RecordingBeaconContext.MemorySet(new List<ProjectImportedDocument>(), saved),
            [typeof(McpDocChunk)] = RecordingBeaconContext.MemorySet(
                [
                    new McpDocChunk { Id = CallerChunkId, ProjectId = CallerProjectId, SourceSectionId = 1, ChunkText = "caller chunk: quarterly close" },
                    new McpDocChunk { Id = OtherChunkId, ProjectId = OtherProjectId, SourceSectionId = 2, ChunkText = "other chunk: quarterly close" }
                ],
                saved),
            [typeof(McpGlossaryTerm)] = RecordingBeaconContext.MemorySet(
                [
                    new McpGlossaryTerm { Id = CallerTermId, ProjectId = CallerProjectId, Term = "Revenue", Definition = "Booked sales." },
                    new McpGlossaryTerm { Id = OtherTermId, ProjectId = OtherProjectId, Term = "Salary mass", Definition = "Total pay." }
                ],
                saved),

            // Every vector is stamped with the caller's project; one of each kind points at the other project's row.
            [typeof(McpEmbedding)] = RecordingBeaconContext.MemorySet(
                [
                    Embedding(100, McpEmbeddingOwnerType.DocChunk, CallerChunkId, chunkVector),
                    Embedding(101, McpEmbeddingOwnerType.DocChunk, OtherChunkId, chunkVector),
                    Embedding(102, McpEmbeddingOwnerType.GlossaryTerm, CallerTermId, glossaryVector),
                    Embedding(103, McpEmbeddingOwnerType.GlossaryTerm, OtherTermId, glossaryVector)
                ],
                saved),
            [typeof(CodeReference)] = RecordingBeaconContext.MemorySet(new List<CodeReference>(), saved),
            [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(new List<DataQualityScore>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InMemoryVectorContext(sets));

        return factory.Object;
    }

    private static DataSource DataSource(int id, string name)
    {
        return new DataSource
        {
            Id = id,
            Name = name,
            DataSourceType = DataSourceType.Database,
            DatabaseEngineType = DatabaseEngineType.PostgreSQL,
            EncryptedConnectionData = "encrypted"
        };
    }

    private static DatabaseMetadata Table(DataSource dataSource, string schema, string table, string description, params string[] columns)
    {
        var metadata = new DatabaseMetadata
        {
            DataSourceId = dataSource.Id,
            DataSource = dataSource,
            SchemaName = schema,
            TableName = table,
            TableDescription = description
        };
        metadata.Columns = columns
            .Select(x => new ColumnMetadata { ColumnName = x, DataType = "text", DatabaseMetadata = metadata })
            .ToList();

        return metadata;
    }

    private static ProjectDocumentationSection Section(int id, Project project, string content)
    {
        return new ProjectDocumentationSection
        {
            Id = id,
            SectionType = ProjectDocSectionType.DataModel,
            Title = "Data model",
            Content = content,
            Documentation = new ProjectDocumentation { ProjectId = project.Id, Project = project }
        };
    }

    private static McpEmbedding Embedding(int id, McpEmbeddingOwnerType ownerType, int ownerId, float[] vector)
    {
        return new McpEmbedding
        {
            Id = id,
            ProjectId = CallerProjectId,
            OwnerType = ownerType,
            OwnerId = ownerId,
            EmbeddingBytes = EmbeddingCodec.ToBytes(vector),
            Model = "bge-small-en-v1.5",
            Dimensions = 384,
            EmbeddingVersion = 1
        };
    }

    /// <summary>Serves the sets from memory and reports a provider other than Npgsql, so retrieval ranks vectors in memory.</summary>
    private sealed class InMemoryVectorContext(IReadOnlyDictionary<Type, object> sets) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<InMemoryVectorContext> Options =
            new DbContextOptionsBuilder<InMemoryVectorContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private DatabaseFacade? _database;

        public override DatabaseFacade Database => _database ??= new OtherProviderFacade(this);

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
            sets.TryGetValue(typeof(TEntity), out var set) ? (DbSet<TEntity>)set : base.Set<TEntity>();

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }

    private sealed class OtherProviderFacade(DbContext context) : DatabaseFacade(context)
    {
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
