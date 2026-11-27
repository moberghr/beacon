using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Beacon.Tests.Unit.Documentation;

/// <summary>
/// One project (linked to one database source) with the documentations, sections and curated glossary terms a test adds,
/// served from in-memory sets (no database, §4.7) to the documentation service, the knowledge queries and the page read.
/// </summary>
internal sealed class DocumentationStore
{
    public const int ProjectId = 5;
    public const int DataSourceId = 9;

    private readonly Project _project;
    private readonly DataSource _dataSource;
    private readonly List<ProjectDocumentation> _documentations = [];
    private readonly List<ProjectDocumentationSection> _sections = [];
    private readonly List<McpGlossaryTerm> _terms = [];
    private readonly List<object> _saved = [];

    public DocumentationStore()
    {
        _project = new Project { Id = ProjectId, Name = "Shop" };
        _dataSource = new DataSource { Id = DataSourceId, Name = "core", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, EncryptedConnectionData = "encrypted" };
        _project.DataSources.Add(new ProjectDataSource { Id = 1, ProjectId = ProjectId, Project = _project, DataSourceId = DataSourceId, DataSource = _dataSource });
    }

    public void AddDocumentation(int id, DateTime generatedAt, params (ProjectDocSectionType Type, string Title, string Content)[] sections)
    {
        var documentation = new ProjectDocumentation { Id = id, ProjectId = ProjectId, Project = _project, GeneratedByModel = "test-model", GeneratedAt = generatedAt };
        var sortOrder = 0;
        foreach (var (type, title, content) in sections)
        {
            var section = new ProjectDocumentationSection
            {
                Id = (id * 100) + sortOrder,
                ProjectDocumentationId = id,
                SectionType = type,
                Title = title,
                Content = content,
                SortOrder = sortOrder,
                Documentation = documentation
            };
            documentation.Sections.Add(section);
            _sections.Add(section);
            sortOrder++;
        }

        _documentations.Add(documentation);
    }

    public void AddTerm(int id, string term, string? synonyms, string definition, bool isActive = true, int projectId = ProjectId)
    {
        _terms.Add(new McpGlossaryTerm { Id = id, ProjectId = projectId, Term = term, Synonyms = synonyms, Definition = definition, IsActive = isActive });
    }

    public BeaconContext Context()
    {
        return new RecordingBeaconContext(
            new Dictionary<Type, object>
            {
                [typeof(Project)] = RecordingBeaconContext.MemorySet([_project], _saved),
                [typeof(DataSource)] = RecordingBeaconContext.MemorySet([_dataSource], _saved),
                [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(_project.DataSources.ToList(), _saved),
                [typeof(ProjectDocumentation)] = RecordingBeaconContext.MemorySet(_documentations, _saved),
                [typeof(ProjectDocumentationSection)] = RecordingBeaconContext.MemorySet(_sections, _saved),
                [typeof(McpGlossaryTerm)] = RecordingBeaconContext.MemorySet(_terms, _saved),
                [typeof(McpLearnedPattern)] = RecordingBeaconContext.MemorySet(new List<McpLearnedPattern>(), _saved)
            },
            _saved);
    }

    public IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => Context());

        return factory.Object;
    }

    public ProjectDocumentationService Service()
    {
        return new ProjectDocumentationService(
            Factory(),
            new Mock<ILlmProvider>(MockBehavior.Strict).Object,
            new Mock<IDataSourceProviderFactory>().Object,
            TestSqlGate.Create(),
            TestConfiguredDatabases.None,
            TestRowSecurity.Resolver,
            Mock.Of<ISharedSourceSettings>(),
            new QueryGuardrailService(),
            TestRowDataEgress.Discarding(),
            new RecordingSecurityAudit(),
            Microsoft.Extensions.Options.Options.Create(new Beacon.Core.Configuration.McpDeploymentOptions()),
            NullLogger<ProjectDocumentationService>.Instance);
    }
}
