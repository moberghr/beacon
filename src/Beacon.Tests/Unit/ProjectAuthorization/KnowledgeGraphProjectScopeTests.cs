using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.AI.Services.SemanticSearch;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.Ai;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Grounding on a data source two projects share, and on one only the other project links: documentation and code
/// references come from the caller's project only, and a data source the caller's project does not link is never searched,
/// described or catalogued. The services run over in-memory sets (no database, §4.7); the lineage and search SQL is
/// captured offline to prove the project predicate reaches the database.
/// </summary>
[TestFixture]
public class KnowledgeGraphProjectScopeTests
{
    private const int SharedDataSourceId = 7;
    private const int OtherProjectsDataSourceId = 8;
    private const int CallerProjectId = 1;
    private const int OtherProjectId = 2;
    private const int UndocumentedProjectId = 3;
    private const string Table = "orders";
    private const string OtherProjectsTable = "payroll";

    [Test]
    public async Task TableKnowledge_DocumentationAndCodeReferences_ComeFromTheCallersProjectOnly()
    {
        var service = BuildService();

        var knowledge = await service.GetTableKnowledgeAsync(SharedDataSourceId, CallerProjectId, "public", Table);

        knowledge.Description.Should().Contain("caller project notes");
        knowledge.Description.Should().NotContain("other project notes");
        knowledge.CodeReferences.Select(x => x.FilePath).Should().Equal("caller/OrderRepository.cs");
    }

    [Test]
    public async Task TableKnowledge_ForTheOtherProject_SeesOnlyItsOwn()
    {
        var service = BuildService();

        var knowledge = await service.GetTableKnowledgeAsync(SharedDataSourceId, OtherProjectId, "public", Table);

        knowledge.Description.Should().Contain("other project notes");
        knowledge.CodeReferences.Select(x => x.FilePath).Should().Equal("other/OrderWriter.cs");
    }

    [Test]
    public async Task TableKnowledge_ForAProjectWithoutDocumentation_HasNoneOfTheOthers()
    {
        var service = BuildService();

        var knowledge = await service.GetTableKnowledgeAsync(SharedDataSourceId, UndocumentedProjectId, "public", Table);

        knowledge.Description.Should().BeNull();
        knowledge.CodeReferences.Should().BeEmpty();
        knowledge.Columns.Select(x => x.Name).Should().Equal("id", "placed_time");
    }

    [Test]
    public async Task Lineage_CodeReferences_ComeFromTheCallersProjectOnly()
    {
        var service = BuildService();

        var lineage = await service.GetLineageAsync(SharedDataSourceId, CallerProjectId, "public", Table);

        lineage.ReadBy.Select(x => x.Name).Should().Equal("OrderRepository");
        lineage.WrittenBy.Should().BeEmpty("the writer belongs to the other project's repository");
    }

    [TestCase(CallerProjectId, true)]
    [TestCase(OtherProjectId, false)]
    public async Task DataSourceKnowledge_CountsCodeReferencesAndDocumentation_OfTheCallersProjectOnly(int projectId, bool documented)
    {
        var service = BuildService();

        var knowledge = await service.GetDataSourceKnowledgeAsync(SharedDataSourceId, projectId);

        knowledge.CodeReferenceCount.Should().Be(1, "each project has one code reference to the shared source");
        knowledge.HasDocumentation.Should().Be(documented, "only the caller's project has generated documentation");
        knowledge.TableCount.Should().Be(1);
    }

    // Every member names the caller's project: a member reading project material or a data source without one would
    // answer for whichever project links the source. None is Admin-only today; one that is would be listed here.
    private static readonly HashSet<string> AdminOnlyMembers = [];

    [TestCase(typeof(IKnowledgeGraphService))]
    [TestCase(typeof(IValueGroundingService))]
    [TestCase(typeof(ISemanticSearchService))]
    public void EveryKnowledgeGraphMember_TakesProjectIdOrIsAdminOnly(Type grounding)
    {
        var withoutProject = grounding
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(x => !AdminOnlyMembers.Contains($"{grounding.Name}.{x.Name}"))
            .Where(x => !x.GetParameters().Any(y => y.Name == "projectId" && y.ParameterType == typeof(int)))
            .Select(x => x.Name)
            .ToList();

        withoutProject.Should().BeEmpty("grounding is filtered by the caller's authorized project, never by data source alone");
    }

    [Test]
    public async Task Search_SearchesOnlyTheDataSourcesTheCallersProjectLinks()
    {
        var service = BuildService();

        var caller = await service.SearchAsync("payroll orders", CallerProjectId);
        var other = await service.SearchAsync("payroll orders", OtherProjectId);

        caller.Select(x => x.DataSourceId).Should().OnlyContain(x => x == SharedDataSourceId);
        caller.Select(x => x.TableName).Should().Contain(Table).And.NotContain(OtherProjectsTable);
        other.Select(x => x.TableName).Should().Contain(Table).And.Contain(OtherProjectsTable);
    }

    [Test]
    public async Task Search_NamingADataSourceTheProjectDoesNotLink_FindsNothing()
    {
        var service = BuildService();

        var results = await service.SearchAsync("payroll", CallerProjectId, OtherProjectsDataSourceId);

        results.Should().BeEmpty();
    }

    [Test]
    public async Task ContextForLlm_ForATable_UsesTheCallersProjectDocumentationOnly()
    {
        var service = BuildService();

        var context = await service.GetContextForLlmAsync(SharedDataSourceId, CallerProjectId, "public", Table);

        context.Should().Contain("caller project notes").And.NotContain("other project notes");
        context.Should().Contain("placed_time");
    }

    [Test]
    public async Task ContextForLlm_ForTheDataSource_ListsItsTablesOnly()
    {
        var service = BuildService();

        var context = await service.GetContextForLlmAsync(SharedDataSourceId, CallerProjectId);

        context.Should().Contain($"public.{Table}").And.NotContain(OtherProjectsTable);
    }

    [Test]
    public async Task ContextForLlm_OnADataSourceTheProjectDoesNotLink_IsNotFound()
    {
        var service = BuildService();

        var act = () => service.GetContextForLlmAsync(OtherProjectsDataSourceId, CallerProjectId, "hr", OtherProjectsTable);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Data source {OtherProjectsDataSourceId} not found");
    }

    [Test]
    public async Task SchemaCatalog_OnlyThroughAProjectThatLinksTheDataSource()
    {
        var service = BuildService();

        var linked = await service.GetSchemaCatalogAsync(OtherProjectsDataSourceId, OtherProjectId);
        var notLinked = await service.GetSchemaCatalogAsync(OtherProjectsDataSourceId, CallerProjectId);

        linked.Should().ContainKey(OtherProjectsTable);
        notLinked.Should().BeEmpty("a catalog the caller's project does not link refuses every table instead of describing it");
    }

    [Test]
    public async Task TablesContext_OnlyThroughAProjectThatLinksTheDataSource()
    {
        var service = BuildService();

        var linked = await service.GetTablesContextAsync(SharedDataSourceId, CallerProjectId, [Table]);
        var notLinked = () => service.GetTablesContextAsync(OtherProjectsDataSourceId, CallerProjectId, [OtherProjectsTable]);

        linked.Should().Contain(Table);
        await notLinked.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task SemanticSearch_ACallerWhoMayNotQueryInTheProject_IsRefusedBeforeAnyLlmCall()
    {
        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        var knowledge = new Mock<IKnowledgeGraphService>(MockBehavior.Strict);
        var viewer = SemanticSearch(TestProjectAccess.ReadableIn(CallerProjectId), knowledge, llm);
        var stranger = SemanticSearch(TestProjectAccess.ReadableIn(OtherProjectId), knowledge, llm);

        var asViewer = () => viewer.AskAsync(SharedDataSourceId, CallerProjectId, "how many orders?");
        var asStranger = () => stranger.AskAsync(SharedDataSourceId, CallerProjectId, "how many orders?");

        await asViewer.Should().ThrowAsync<BeaconForbiddenException>();
        await asStranger.Should().ThrowAsync<BeaconNotFoundException>();
        knowledge.VerifyNoOtherCalls();
        llm.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SemanticSearch_GroundsTheQuestionInTheCallersProject()
    {
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new LlmResponse { Content = "no sql" });
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetContextForLlmAsync(SharedDataSourceId, CallerProjectId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("schema");
        var service = SemanticSearch(TestProjectAccess.Unrestricted, knowledge, llm);

        await service.AskAsync(SharedDataSourceId, CallerProjectId, "how many orders?", execute: false);

        knowledge.Verify(x => x.GetContextForLlmAsync(SharedDataSourceId, CallerProjectId, null, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task SemanticSearch_OfADataSourceTheProjectDoesNotLink_IsRefused_NotAnsweredWithTheRefusalsText()
    {
        var llm = new Mock<ILlmProvider>(MockBehavior.Strict);
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetContextForLlmAsync(OtherProjectsDataSourceId, CallerProjectId, null, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException($"Data source {OtherProjectsDataSourceId} not found."));
        var service = SemanticSearch(TestProjectAccess.Unrestricted, knowledge, llm);

        var act = () => service.AskAsync(OtherProjectsDataSourceId, CallerProjectId, "how many orders?");

        await act.Should().ThrowAsync<InvalidOperationException>();
        llm.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SemanticSearch_ThatFails_AnswersAGenericMessage_NeverTheExceptionsText()
    {
        var llm = new Mock<ILlmProvider>();
        llm
            .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("upstream echoed: SELECT iban FROM accounts WHERE owner = 'Ada'"));
        var knowledge = new Mock<IKnowledgeGraphService>();
        knowledge
            .Setup(x => x.GetContextForLlmAsync(SharedDataSourceId, CallerProjectId, null, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync("schema");
        var service = SemanticSearch(TestProjectAccess.Unrestricted, knowledge, llm);

        var result = await service.AskAsync(SharedDataSourceId, CallerProjectId, "how many orders?");

        result.Error.Should().Be(SemanticSearchService.FailedMessage);
        result.Error.Should().NotContain("Ada");
    }

    [Test]
    public async Task Search_ProjectPredicate_ReachesTheSql()
    {
        var capture = new SqlCapture();
        var service = Service(capture.Factory());

        var act = () => service.SearchAsync("orders", CallerProjectId);

        await act.Should().ThrowAsync<SqlCapturedException>();
        capture.Commands.Should().ContainSingle();
        capture.Commands[0].Should().Contain("project_data_sources").And.Contain("project_id");
        capture.CommandParameters[0].Values.Should().Contain(CallerProjectId);
    }

    [Test]
    public async Task Lineage_ProjectPredicate_ReachesTheSql()
    {
        var capture = new SqlCapture().ThenScalar(true);
        var service = Service(capture.Factory());

        var act = () => service.GetLineageAsync(SharedDataSourceId, CallerProjectId, "public", Table);

        await act.Should().ThrowAsync<SqlCapturedException>();
        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain("project_data_sources").And.Contain("project_id");
        capture.CommandParameters[0].Values.Should().Contain(CallerProjectId).And.Contain(SharedDataSourceId);
        capture.Commands[1].Should().Contain("g.project_id = @projectId");
        capture.CommandParameters[1]["@projectId"].Should().Be(CallerProjectId);
    }

    // Table knowledge, a data source's overview and lineage read a data source only through a project that links it.
    [Test]
    public async Task TableKnowledge_DataSourceKnowledge_AndLineage_OfADataSourceTheProjectDoesNotLink_AreNotFound()
    {
        var service = BuildService();

        var table = () => service.GetTableKnowledgeAsync(OtherProjectsDataSourceId, CallerProjectId, "hr", OtherProjectsTable);
        var overview = () => service.GetDataSourceKnowledgeAsync(OtherProjectsDataSourceId, CallerProjectId);
        var lineage = () => service.GetLineageAsync(OtherProjectsDataSourceId, CallerProjectId, "hr", OtherProjectsTable);

        await table.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Data source {OtherProjectsDataSourceId} not found");
        await overview.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Data source {OtherProjectsDataSourceId} not found");
        await lineage.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Data source {OtherProjectsDataSourceId} not found");
        (await service.GetTableKnowledgeAsync(OtherProjectsDataSourceId, OtherProjectId, "hr", OtherProjectsTable)).TableName.Should().Be(OtherProjectsTable);
        (await service.GetDataSourceKnowledgeAsync(OtherProjectsDataSourceId, OtherProjectId)).Name.Should().Be("hr");
        (await service.GetLineageAsync(OtherProjectsDataSourceId, OtherProjectId, "hr", OtherProjectsTable)).TableName.Should().Be(OtherProjectsTable);
    }

    private static KnowledgeGraphService BuildService()
    {
        var dataSource = new DataSource
        {
            Id = SharedDataSourceId,
            Name = "warehouse",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };
        var otherProjectsDataSource = new DataSource
        {
            Id = OtherProjectsDataSourceId,
            Name = "hr",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "encrypted",
            DatabaseEngineType = DatabaseEngineType.PostgreSQL
        };
        var callerProject = Project(CallerProjectId);
        var otherProject = Project(OtherProjectId);
        var orders = new DatabaseMetadata
        {
            DataSourceId = SharedDataSourceId,
            DataSource = dataSource,
            SchemaName = "public",
            TableName = Table,
            TableDescription = "orders placed",
            Columns =
            [
                new ColumnMetadata { ColumnName = "id", DataType = "int", IsPrimaryKey = true },
                new ColumnMetadata { ColumnName = "placed_time", DataType = "timestamp", IsNullable = true }
            ]
        };
        var payroll = new DatabaseMetadata
        {
            DataSourceId = OtherProjectsDataSourceId,
            DataSource = otherProjectsDataSource,
            SchemaName = "hr",
            TableName = OtherProjectsTable,
            TableDescription = "payroll orders",
            Columns = [new ColumnMetadata { ColumnName = "salary", DataType = "numeric" }]
        };
        var columns = new[] { orders, payroll }
            .SelectMany(x => x.Columns.Select(y =>
            {
                y.DatabaseMetadata = x;
                return y;
            }))
            .ToList();
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource, otherProjectsDataSource], saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet([orders, payroll], saved),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(
                [
                    new ProjectDataSource { ProjectId = CallerProjectId, DataSourceId = SharedDataSourceId },
                    new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = SharedDataSourceId },
                    new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = OtherProjectsDataSourceId },
                    new ProjectDataSource { ProjectId = UndocumentedProjectId, DataSourceId = SharedDataSourceId }
                ],
                saved),
            [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(
                [
                    DataModelSection(callerProject, "orders: caller project notes", generatedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)),
                    DataModelSection(otherProject, "orders: other project notes", generatedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc))
                ],
                saved),
            [typeof(CodeReference)] = RecordingBeaconContext.MemorySet(
                [
                    CodeReference(callerProject, "caller/OrderRepository.cs", "OrderRepository", CodeReferenceType.EntityModel),
                    CodeReference(otherProject, "other/OrderWriter.cs", "OrderWriter", CodeReferenceType.RawSql)
                ],
                saved),
            [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet([new ProjectDocumentation { ProjectId = CallerProjectId, Project = callerProject }], saved),
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(columns, saved),
            [typeof(DataQualityScore)] = RecordingBeaconContext.MemorySet(new List<DataQualityScore>(), saved),
            [typeof(DataContract)] = RecordingBeaconContext.MemorySet(new List<DataContract>(), saved),
            [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(new List<QueryExecutionHistory>(), saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return Service(factory.Object);
    }

    private static SemanticSearchService SemanticSearch(
        ProjectAccessScope scope,
        Mock<IKnowledgeGraphService> knowledge,
        Mock<ILlmProvider> llm)
    {
        return new SemanticSearchService(
            Mock.Of<IDbContextFactory<BeaconContext>>(),
            knowledge.Object,
            llm.Object,
            Mock.Of<IQueryGuardrailService>(),
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            Mock.Of<IDataSourceProviderFactory>(),
            TestProjectAccess.Evaluator(scope),
            TestProjectAccess.Actors(),
            NullLogger<SemanticSearchService>.Instance);
    }

    private static KnowledgeGraphService Service(IDbContextFactory<BeaconContext> factory)
    {
        return new KnowledgeGraphService(
            factory,
            SettingsProviderMock.Create().Object,
            SettingsProviderMock.Unshared(SettingsProviderMock.Create().Object),
            Mock.Of<IBeaconEmbeddingService>(),
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IValueGroundingService>(),
            TestConfiguredDatabases.None,
            new QueryGuardrailService(),
            NullLogger<KnowledgeGraphService>.Instance);
    }

    // Both projects link the shared data source.
    private static Project Project(int id)
    {
        var project = new Project { Id = id, Name = $"project-{id}" };
        project.DataSources.Add(new ProjectDataSource { ProjectId = id, DataSourceId = SharedDataSourceId, Project = project });

        return project;
    }

    private static ProjectDocumentationSection DataModelSection(Project project, string content, DateTime generatedAt)
    {
        return new ProjectDocumentationSection
        {
            SectionType = ProjectDocSectionType.DataModel,
            Title = "Data model",
            Content = content,
            Documentation = new ProjectDocumentation
            {
                ProjectId = project.Id,
                Project = project,
                GeneratedAt = generatedAt
            }
        };
    }

    private static CodeReference CodeReference(Project project, string filePath, string className, CodeReferenceType type)
    {
        return new CodeReference
        {
            FilePath = filePath,
            TableName = Table,
            ClassName = className,
            ReferenceType = type,
            GitHubRepository = new GitHubRepository
            {
                ProjectId = project.Id,
                Project = project,
                RepositoryUrl = $"https://example.invalid/{project.Name}"
            }
        };
    }
}
