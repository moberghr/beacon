using System.Collections;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Documentation;

/// <summary>
/// Regenerating a project's documentation keeps what people curated: a section edited in the latest documentation (by
/// hand or by an applied patch) replaces what the run generated for its type, still marked with who edited it and when;
/// a section that failed to generate holds a fixed text, never the failure's message; and the generation is recorded in
/// the security audit by the user who started it, with counts only. Real service over in-memory sets, no database (§4.7).
/// </summary>
[TestFixture]
public class DocumentationRegenerationTests
{
    private const int ProjectId = 4;
    private const int DataSourceId = 9;
    private const int CuratorUserId = 42;
    private const int StarterUserId = 17;
    private const string ProviderFailure = "provider said: quota exceeded for key sk-test";

    private static readonly DateTime EditedTime = new(2026, 10, 20, 8, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task Regenerating_KeepsTheCuratedSections_OfTheLatestDocumentation_StillMarkedCurated()
    {
        var world = new World();

        await world.Service().GenerateDocumentationAsync(ProjectId, StarterUserId, CancellationToken.None);

        var generated = world.Saved.OfType<ProjectDocumentation>().Should().ContainSingle().Subject;
        var dataModel = generated.Sections.Should().ContainSingle(x => x.SectionType == ProjectDocSectionType.DataModel).Subject;
        dataModel.Should().BeEquivalentTo(
            new { Title = "Data Model", Content = "Orders belong to customers (curated).", EditedByUserId = (int?)CuratorUserId, EditedTime = (DateTime?)EditedTime },
            x => x.ExcludingMissingMembers());
        generated.Sections.Single(x => x.SectionType == ProjectDocSectionType.ProjectOverview).EditedTime
            .Should().BeNull("a section nobody edited is regenerated");
        generated.Sections.Should().NotContain(x => x.Content.Contains("older curated text"), "only the latest documentation's edits are carried");
        generated.Sections.Select(x => x.SectionType).Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task Regenerating_KeepsACuratedSectionOfATypeThisRunDidNotGenerate()
    {
        var world = new World();
        world.Latest.Sections.Add(world.LatestSection(13, ProjectDocSectionType.ApiDocumentation, "API Documentation", "Curated API notes.", edited: true));

        await world.Service().GenerateDocumentationAsync(ProjectId, StarterUserId, CancellationToken.None);

        var generated = world.Saved.OfType<ProjectDocumentation>().Single();
        generated.Sections.Should().ContainSingle(x => x.SectionType == ProjectDocSectionType.ApiDocumentation)
            .Which.Content.Should().Be("Curated API notes.");
    }

    [Test]
    public async Task ASectionThatFailsToGenerate_HoldsAFixedText_NeverTheFailuresMessage()
    {
        var world = new World();

        await world.Service().GenerateDocumentationAsync(ProjectId, StarterUserId, CancellationToken.None);

        var generated = world.Saved.OfType<ProjectDocumentation>().Single();
        generated.Sections
            .Where(x => x.EditedTime == null)
            .Should().NotBeEmpty()
            .And.OnlyContain(x => x.Content == ProjectDocumentationService.FailedSectionContent);
        generated.Sections.Should().NotContain(x => x.Content.Contains("quota") || x.Content.Contains("sk-test"));
    }

    [Test]
    public async Task AGeneration_IsRecorded_ByTheUserWhoStartedIt_WithCountsOnly()
    {
        var world = new World();

        await world.Service().GenerateDocumentationAsync(ProjectId, StarterUserId, CancellationToken.None);

        var generated = world.Saved.OfType<ProjectDocumentation>().Single();
        var recorded = world.Audit.Single("Orders belong to customers", ProviderFailure);
        recorded.Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DocumentationGenerated, ActorUserId = (int?)StarterUserId, ResourceType = SecurityAuditResources.Project, ResourceId = ProjectId.ToString(), ProjectId = (int?)ProjectId },
            x => x.ExcludingMissingMembers());
        recorded.DetailsJson.Should().Be(
            $"{{\"documentationId\":{generated.Id},\"sectionCount\":{generated.Sections.Count},\"curatedSectionsKept\":1,\"failedSections\":7}}");
        world.Audit.Ways.Should().Equal("saved");
    }

    [Test]
    public async Task AGenerationWithoutAStoredStarter_IsRecordedAsTheSystem()
    {
        var world = new World();

        await world.Service().GenerateDocumentationAsync(ProjectId, 0, CancellationToken.None);

        world.Audit.Of(SecurityAuditActions.DocumentationGenerated).Should().ContainSingle()
            .Which.ActorKind.Should().Be(SecurityAuditActorKinds.System);
    }

    [Test]
    public void CarryingCuratedSections_ReplacesTheGeneratedSectionOfTheirType_AndKeepsItsPlace()
    {
        var sections = new List<ProjectDocumentationSection>
        {
            new() { SectionType = ProjectDocSectionType.ProjectOverview, Title = "Project Overview", Content = "generated overview", SortOrder = 0 },
            new() { SectionType = ProjectDocSectionType.DataModel, Title = "Data Model", Content = "generated model", SortOrder = 2 }
        };
        var curated = new List<ProjectDocumentationSection>
        {
            new() { Id = 5, SectionType = ProjectDocSectionType.DataModel, Title = "Data Model", Content = "curated model", SortOrder = 9, EditedByUserId = CuratorUserId, EditedTime = EditedTime }
        };

        var carried = ProjectDocumentationService.CarryCuratedSections(sections, curated);

        carried.Should().Be(1);
        sections.Select(x => (x.SectionType, x.Content, x.SortOrder, x.EditedByUserId, x.Id)).Should().Equal(
            (ProjectDocSectionType.ProjectOverview, "generated overview", 0, (int?)null, 0),
            (ProjectDocSectionType.DataModel, "curated model", 2, (int?)CuratorUserId, 0));
    }

    /// <summary>
    /// Project 4 with one database source and no tables, its latest documentation (11: an edited Data Model section and an
    /// unedited Overview) and an older one (10, whose edited Data Model is superseded); a model that always fails.
    /// </summary>
    private sealed class World
    {
        private readonly Dictionary<Type, IList> _sets;

        public World()
        {
            var dataSource = new DataSource { Id = DataSourceId, Name = "core", DataSourceType = DataSourceType.Database, DatabaseEngineType = DatabaseEngineType.PostgreSQL, EncryptedConnectionData = "encrypted" };
            var project = new Project { Id = ProjectId, Name = "p" };
            var older = new ProjectDocumentation { Id = 10, ProjectId = ProjectId, GeneratedByModel = "m", GeneratedAt = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) };
            Latest = new ProjectDocumentation { Id = 11, ProjectId = ProjectId, GeneratedByModel = "m", GeneratedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc) };
            Latest.Sections.Add(LatestSection(12, ProjectDocSectionType.DataModel, "Data Model", "Orders belong to customers (curated).", edited: true));
            Latest.Sections.Add(LatestSection(14, ProjectDocSectionType.ProjectOverview, "Project Overview", "Old generated overview.", edited: false));
            older.Sections.Add(new ProjectDocumentationSection { Id = 15, ProjectDocumentationId = 10, SectionType = ProjectDocSectionType.DataModel, Title = "Data Model", Content = "older curated text", EditedByUserId = CuratorUserId, EditedTime = EditedTime.AddDays(-30), Documentation = older });
            _sets = new Dictionary<Type, IList>
            {
                [typeof(DataSource)] = new List<DataSource> { dataSource },
                [typeof(Project)] = new List<Project> { project },
                [typeof(ProjectDataSource)] = new List<ProjectDataSource> { new() { Id = 1, ProjectId = ProjectId, Project = project, DataSourceId = DataSourceId, DataSource = dataSource } },
                [typeof(ProjectDocumentation)] = new List<ProjectDocumentation> { older, Latest }
            };
        }

        public ProjectDocumentation Latest { get; }

        public List<object> Saved { get; } = [];

        public RecordingSecurityAudit Audit { get; } = new();

        public ProjectDocumentationSection LatestSection(int id, ProjectDocSectionType type, string title, string content, bool edited)
        {
            return new ProjectDocumentationSection
            {
                Id = id,
                ProjectDocumentationId = Latest.Id,
                SectionType = type,
                Title = title,
                Content = content,
                SortOrder = 20,
                EditedByUserId = edited ? CuratorUserId : null,
                EditedTime = edited ? EditedTime : null,
                Documentation = Latest
            };
        }

        public ProjectDocumentationService Service()
        {
            _sets[typeof(ProjectDocumentationSection)] = _sets[typeof(ProjectDocumentation)]
                .Cast<ProjectDocumentation>()
                .SelectMany(x => x.Sections)
                .ToList();
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new ServedContext(_sets, Saved));
            var llm = new Mock<ILlmProvider>();
            llm
                .Setup(x => x.CompleteAsync(It.IsAny<LlmRequest>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException(ProviderFailure));
            var shared = new Mock<ISharedSourceSettings>();
            shared
                .Setup(x => x.GetEffectiveSettingsAsync(ProjectId, It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new McpSettingsData());

            return new ProjectDocumentationService(
                factory.Object,
                llm.Object,
                new Mock<IDataSourceProviderFactory>().Object,
                TestSqlGate.Create(),
                TestConfiguredDatabases.None,
                TestRowSecurity.Resolver,
                shared.Object,
                new QueryGuardrailService(),
                TestRowDataEgress.Discarding(),
                Audit,
                NullLogger<ProjectDocumentationService>.Instance);
        }
    }

    // Serves the listed sets (every other one empty); Add records into saved, and a save gives the added documentation its id.
    private sealed class ServedContext(Dictionary<Type, IList> sets, List<object> saved) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<ServedContext> Options =
            new DbContextOptionsBuilder<ServedContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>()
        {
            var rows = sets.TryGetValue(typeof(TEntity), out var list)
                ? list.Cast<TEntity>().ToList()
                : new List<TEntity>();

            return RecordingBeaconContext.MemorySet(rows, saved);
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            foreach (var documentation in saved.OfType<ProjectDocumentation>())
            {
                documentation.Id = 30;
            }

            return Task.FromResult(1);
        }
    }
}
