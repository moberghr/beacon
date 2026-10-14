using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.RowData;

/// <summary>
/// A knowledge answer carries the project's search results, whose snippets are the stored text of documentation sections.
/// A section that can list values discovered in the data (a data model here) may hold a row value written before the
/// row-data lock: under the lock its search results — from the keyword arm and from the semantic arm — stay out of the
/// prompt, and without the lock they go in and the request is logged with the stored-documentation category. The real
/// project search runs over in-memory rows (§4.7: no database).
/// </summary>
[TestFixture]
public class KnowledgeAnswerSearchResultTests
{
    private const int ProjectId = 41;
    private const int OverviewSectionId = 1;
    private const int ModelSectionId = 2;
    private const string RowValue = "alice.horvat@bank.example";

    // In the text of both sections, so the keyword arm finds them.
    private const string KeywordQuestion = "ledger";

    // In no stored text, so only the semantic arm finds the chunks (their vectors are this question's).
    private const string SemanticQuestion = "quarterly close";

    [TestCase(false)]
    [TestCase(true)]
    public async Task UnderTheRowDataLock_AValueListingSectionsSearchResult_StaysOutOfThePrompt(bool semantic)
    {
        var log = new LogRecorder();
        var (service, llm) = Build(semantic, forbidRowData: true, log);

        var answer = await service.AnswerAsync(llm, ProjectId, Question(semantic), new McpSettingsData(), CancellationToken.None);

        answer.Should().Contain("## Relevant Search Results").And.Contain("ledger overview");
        answer.Should().NotContain(RowValue);
        log.Entries.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task WithoutTheLock_AValueListingSectionsSearchResult_IsSentAndLogged(bool semantic)
    {
        var log = new LogRecorder();
        var (service, llm) = Build(semantic, forbidRowData: false, log);

        var answer = await service.AnswerAsync(llm, ProjectId, Question(semantic), new McpSettingsData(), CancellationToken.None);

        answer.Should().Contain("ledger overview").And.Contain(RowValue);
        log.Entries.Should().ContainSingle()
            .Which.Message.Should().Be($"An LLM request carrying stored-documentation read from data sources  of project {ProjectId} was handed to the provider for caller {TestRowDataEgress.CallerId}");
    }

    // Both arms name the section each documentation result comes from; nothing else carries a section.
    [TestCase(false)]
    [TestCase(true)]
    public async Task ProjectSearch_NamesTheSectionOfEachDocumentationResult(bool semantic)
    {
        var results = await KnowledgeGraph(semantic).SearchProjectAsync(Question(semantic), ProjectId);

        results.Should().OnlyContain(x => x.Type == "documentation");
        results.Select(x => (x.DocIdentity, x.SectionType)).Should().BeEquivalentTo(
        [
            ($"docsection:{OverviewSectionId}", (ProjectDocSectionType?)ProjectDocSectionType.ProjectOverview),
            ($"docsection:{ModelSectionId}", (ProjectDocSectionType?)ProjectDocSectionType.DataModel)
        ]);
    }

    private static string Question(bool semantic)
    {
        return semantic ? SemanticQuestion : KeywordQuestion;
    }

    // The answer service over the real project search; no documentation export, chunk retrieval or schema context, so the
    // search results are all the prompt carries. The provider echoes the prompt back.
    private static (KnowledgeAnswerService Service, ILlmProvider Llm) Build(bool semantic, bool forbidRowData, LogRecorder log)
    {
        var search = KnowledgeGraph(semantic);
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.SearchProjectAsync(It.IsAny<string>(), ProjectId, It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns((string query, int projectId, int maxResults, CancellationToken ct) => search.SearchProjectAsync(query, projectId, maxResults, ct));
        knowledgeGraph
            .Setup(x => x.GetRelevantDocChunksAsync(ProjectId, It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        knowledgeGraph
            .Setup(x => x.GetProjectContextForLlmAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var documentation = new Mock<IProjectDocumentationService>();
        documentation
            .Setup(x => x.ExportLatestToMarkdownAsync(ProjectId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);
        documentation
            .Setup(x => x.ExportLatestToMarkdownAsync(ProjectId, It.IsAny<IReadOnlyCollection<ProjectDocSectionType>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((LlmRequest request, CancellationToken _) => new LlmResponse { Content = request.Messages[0].Content });

        var service = new KnowledgeAnswerService(
            knowledgeGraph.Object,
            documentation.Object,
            TestRowDataEgress.Logging(log.For<LlmRowDataEgress>()),
            Options.Create(new McpDeploymentOptions { ForbidRowDataToLlm = forbidRowData }));

        return (service, llm.Object);
    }

    private static KnowledgeGraphService KnowledgeGraph(bool semantic)
    {
        var settings = SettingsProviderMock.Create(new McpSettingsData { EnableSemanticRetrieval = true });

        return new KnowledgeGraphService(
            World(),
            settings.Object,
            SettingsProviderMock.Unshared(settings.Object),
            semantic ? new FakeEmbeddingService() : Mock.Of<IBeaconEmbeddingService>(),
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance);
    }

    // A project with an overview section and a data-model section generated before the lock, one chunk of each, and the
    // chunks' vectors.
    private static IDbContextFactory<BeaconContext> World()
    {
        var project = new Project { Id = ProjectId, Name = "Finance" };
        var documentation = new ProjectDocumentation { ProjectId = ProjectId, Project = project };
        var vector = new FakeEmbeddingService().EmbedAsync(SemanticQuestion, CancellationToken.None).GetAwaiter().GetResult();

        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(Project)] = RecordingBeaconContext.MemorySet([project], saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(new List<ProjectDataSource>(), saved),
            [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(
                [
                    Section(OverviewSectionId, ProjectDocSectionType.ProjectOverview, "The ledger overview: the shop books its sales.", documentation),
                    Section(ModelSectionId, ProjectDocSectionType.DataModel, $"The ledger's customer_email lists {RowValue} and more.", documentation)
                ],
                saved),
            [typeof(ProjectImportedDocument)] = RecordingBeaconContext.MemorySet(new List<ProjectImportedDocument>(), saved),
            [typeof(McpDocChunk)] = RecordingBeaconContext.MemorySet(
                [
                    new McpDocChunk { Id = 10, ProjectId = ProjectId, SourceSectionId = OverviewSectionId, ChunkText = "ledger overview: the shop books its sales" },
                    new McpDocChunk { Id = 11, ProjectId = ProjectId, SourceSectionId = ModelSectionId, ChunkText = $"customer_email lists {RowValue}" }
                ],
                saved),
            [typeof(McpEmbedding)] = RecordingBeaconContext.MemorySet(
                [
                    Embedding(100, 10, vector),
                    Embedding(101, 11, vector)
                ],
                saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new InMemoryVectorContext(sets));

        return factory.Object;
    }

    private static ProjectDocumentationSection Section(int id, ProjectDocSectionType type, string content, ProjectDocumentation documentation)
    {
        return new ProjectDocumentationSection
        {
            Id = id,
            SectionType = type,
            Title = type.ToString(),
            Content = content,
            Documentation = documentation
        };
    }

    private static McpEmbedding Embedding(int id, int chunkId, float[] vector)
    {
        return new McpEmbedding
        {
            Id = id,
            ProjectId = ProjectId,
            OwnerType = McpEmbeddingOwnerType.DocChunk,
            OwnerId = chunkId,
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
    }

    private sealed class OtherProviderFacade(DbContext context) : DatabaseFacade(context)
    {
        public override string? ProviderName => "Microsoft.EntityFrameworkCore.SqlServer";
    }
}
