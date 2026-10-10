using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Embeddings;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.DataQuality;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Table knowledge, lineage and the data-source overview on a data source two projects share: documentation and code
/// references come from the caller's project only. The services run over in-memory sets (no database, §4.7); the lineage SQL is captured offline
/// to prove the project predicate reaches the database.
/// </summary>
[TestFixture]
public class KnowledgeGraphProjectScopeTests
{
    private const int SharedDataSourceId = 7;
    private const int CallerProjectId = 1;
    private const int OtherProjectId = 2;
    private const string Table = "orders";

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

        var knowledge = await service.GetTableKnowledgeAsync(SharedDataSourceId, 99, "public", Table);

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

    [Test]
    public async Task Lineage_ProjectPredicate_ReachesTheSql()
    {
        var capture = new SqlCapture();
        var service = Service(capture.Factory());

        var act = () => service.GetLineageAsync(SharedDataSourceId, CallerProjectId, "public", Table);

        await act.Should().ThrowAsync<SqlCapturedException>();
        capture.Commands.Should().ContainSingle();
        capture.Commands[0].Should().Contain("g.project_id = @projectId");
        capture.CommandParameters[0]["@projectId"].Should().Be(CallerProjectId);
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
        var callerProject = Project(CallerProjectId);
        var otherProject = Project(OtherProjectId);
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(DataSource)] = RecordingBeaconContext.MemorySet([dataSource], saved),
            [typeof(DatabaseMetadata)] = RecordingBeaconContext.MemorySet(
                [
                    new DatabaseMetadata
                    {
                        DataSourceId = SharedDataSourceId,
                        SchemaName = "public",
                        TableName = Table,
                        Columns =
                        [
                            new ColumnMetadata { ColumnName = "id", DataType = "int", IsPrimaryKey = true },
                            new ColumnMetadata { ColumnName = "placed_time", DataType = "timestamp", IsNullable = true }
                        ]
                    }
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
            [typeof(ColumnMetadata)] = RecordingBeaconContext.MemorySet(new List<ColumnMetadata>(), saved),
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

    private static KnowledgeGraphService Service(IDbContextFactory<BeaconContext> factory)
    {
        return new KnowledgeGraphService(
            factory,
            SettingsProviderMock.Create().Object,
            Mock.Of<IBeaconEmbeddingService>(),
            Mock.Of<ISchemaGraphService>(),
            Mock.Of<IValueGroundingService>(),
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
