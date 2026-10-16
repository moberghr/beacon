using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Learning;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Documentation patches are proposed from learned patterns project by project (§1.12): two projects share data
/// source 1, and each project's patches come only from its own patterns, only for the data sources it links today (an
/// archived project links none), and are deduplicated only against its own patches. Driven through
/// <c>TestAsyncQueryable</c>-backed DbSets (§4.7); the per-project reads' predicates are pinned in their SQL on both
/// providers.
/// </summary>
[TestFixture]
public class DocumentationPatchProjectScopingTests
{
    private const int ProjectA = 1;
    private const int ProjectB = 2;
    private const int SharedDataSourceId = 1;
    private const string Target = "public.orders.status";
    private const string ContentA = "pattern-owned-by-project-a";
    private const string ContentB = "pattern-owned-by-project-b";

    [Test]
    public async Task GenerateDocumentationPatches_SharedDataSource_ProposesProjectBsPatchFromProjectBsPatternOnly()
    {
        // Project A already has a proposed patch for the shared column; project B has its own pattern for it.
        var patterns = new List<McpLearnedPattern>
        {
            Correction(ProjectA, ContentA),
            Correction(ProjectB, ContentB)
        };
        var existing = new List<McpDocumentationPatch>
        {
            new()
            {
                ProjectId = ProjectA,
                DataSourceId = SharedDataSourceId,
                TargetType = McpDocPatchTarget.ColumnDescription,
                TargetIdentifier = Target,
                ProposedContent = ContentA,
                Reasoning = "earlier run",
                Status = McpDocPatchStatus.Proposed
            }
        };
        var added = new List<McpDocumentationPatch>();
        var service = BuildService(new PatchContextFactory(patterns, existing, added, [Link(ProjectA), Link(ProjectB)]));

        await service.GenerateDocumentationPatchesAsync(CancellationToken.None);

        var patch = added.Should().ContainSingle().Subject;
        patch.ProjectId.Should().Be(ProjectB);
        patch.ProposedContent.Should().Be(ContentB);
        patch.Reasoning.Should().NotContain(ContentA);
    }

    [Test]
    public async Task GenerateDocumentationPatches_AProjectThatUnlinkedTheSource_GetsNone()
    {
        // Both projects own a pattern on the shared source; only project A still links it, project B links another source.
        var patterns = new List<McpLearnedPattern> { Correction(ProjectA, ContentA), Correction(ProjectB, ContentB) };
        var added = new List<McpDocumentationPatch>();
        var service = BuildService(new PatchContextFactory(patterns, [], added, [Link(ProjectA), Link(ProjectB, dataSourceId: SharedDataSourceId + 1)]));

        await service.GenerateDocumentationPatchesAsync(CancellationToken.None);

        var patch = added.Should().ContainSingle().Subject;
        patch.ProjectId.Should().Be(ProjectA);
        patch.ProposedContent.Should().Be(ContentA);
    }

    [Test]
    public async Task GenerateDocumentationPatches_AnArchivedProjectKeepsItsLinks_ButGetsNone()
    {
        var patterns = new List<McpLearnedPattern> { Correction(ProjectA, ContentA), Correction(ProjectB, ContentB) };
        var added = new List<McpDocumentationPatch>();
        var service = BuildService(new PatchContextFactory(patterns, [], added, [Link(ProjectA), Link(ProjectB, archived: true)]));

        await service.GenerateDocumentationPatchesAsync(CancellationToken.None);

        added.Should().ContainSingle().Which.ProjectId.Should().Be(ProjectA);
    }

    // The per-project link read carries the project and live-project predicates, and the pattern read the project and the
    // linked data sources, on both providers.
    [TestCase(false, "p.project_id = @projectId", "archived_time IS NULL", "m.project_id = @projectId", "m.data_source_id = ANY (@linkedDataSourceIds)")]
    [TestCase(true, "[p].[ProjectId] = @projectId", "[ArchivedTime] IS NULL", "[m].[ProjectId] = @projectId", "[m].[DataSourceId] IN (@linkedDataSourceIds1, @linkedDataSourceIds2)")]
    public async Task GenerateDocumentationPatches_ReadsCarryTheProjectAndLinkPredicates_Translates(
        bool sqlServer, string linkProject, string live, string patternProject, string linkedSources)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenRow(ProjectB)
            .ThenRows([SharedDataSourceId], [SharedDataSourceId + 1])
            .ThenNoRows();

        await BuildService(capture.Factory()).GenerateDocumentationPatchesAsync(CancellationToken.None);

        capture.Commands.Should().HaveCount(3);
        capture.Commands[1].Should().Contain(linkProject).And.Contain(live);
        capture.CommandParameters[1].Values.Should().Contain(ProjectB);
        capture.Commands[2].Should().Contain(patternProject).And.Contain(linkedSources);
        capture.CommandParameters[2].Values.Should().Contain(ProjectB);
    }

    private static McpLearningAggregationService BuildService(IDbContextFactory<BeaconContext> factory)
    {
        return new McpLearningAggregationService(
            factory,
            SettingsProviderMock.Create().Object,
            TestGroundingSqlChecks.PassThrough().Object,
            NullLogger<McpLearningAggregationService>.Instance,
            lessonExtractor: null,
            replayVerifier: null);
    }

    private static ProjectDataSource Link(int projectId, bool archived = false, int dataSourceId = SharedDataSourceId)
    {
        return new ProjectDataSource
        {
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Project = new Project { Id = projectId, Name = $"project {projectId}", ArchivedTime = archived ? DateTime.UtcNow : null }
        };
    }

    private static McpLearnedPattern Correction(int projectId, string content)
    {
        return new McpLearnedPattern
        {
            ProjectId = projectId,
            DataSourceId = SharedDataSourceId,
            SchemaName = "public",
            TableName = "orders",
            ColumnName = "status",
            PatternType = McpPatternType.SchemaCorrection,
            PatternContent = content,
            Confidence = 0.9,
            SignalCount = 3,
            Status = McpPatternStatus.Approved
        };
    }

    private sealed class PatchContextFactory(
        List<McpLearnedPattern> patterns,
        List<McpDocumentationPatch> existing,
        List<McpDocumentationPatch> added,
        List<ProjectDataSource> links) : IDbContextFactory<BeaconContext>
    {
        public BeaconContext CreateDbContext() => new PatchContext(patterns, existing, added, links);
    }

    private sealed class PatchContext : BeaconContext
    {
        private static readonly DbContextOptions<PatchContext> Options =
            new DbContextOptionsBuilder<PatchContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly DbSet<McpLearnedPattern> _patterns;
        private readonly DbSet<McpDocumentationPatch> _patches;
        private readonly DbSet<ColumnMetadata> _columns;
        private readonly DbSet<DatabaseMetadata> _tables;
        private readonly DbSet<ProjectDataSource> _links;

        public PatchContext(
            List<McpLearnedPattern> patterns,
            List<McpDocumentationPatch> existing,
            List<McpDocumentationPatch> added,
            List<ProjectDataSource> links) : base(Options, "beacon")
        {
            _links = BuildSet(links).Object;
            _patterns = BuildSet(patterns).Object;
            var patches = BuildSet(existing);
            patches
                .Setup(x => x.Add(It.IsAny<McpDocumentationPatch>()))
                .Callback<McpDocumentationPatch>(added.Add);
            _patches = patches.Object;
            _columns = BuildSet(new List<ColumnMetadata>()).Object;
            _tables = BuildSet(new List<DatabaseMetadata>()).Object;
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpLearnedPattern))
            {
                return (DbSet<TEntity>)(object)_patterns;
            }

            if (typeof(TEntity) == typeof(McpDocumentationPatch))
            {
                return (DbSet<TEntity>)(object)_patches;
            }

            if (typeof(TEntity) == typeof(ColumnMetadata))
            {
                return (DbSet<TEntity>)(object)_columns;
            }

            if (typeof(TEntity) == typeof(DatabaseMetadata))
            {
                return (DbSet<TEntity>)(object)_tables;
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)_links;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private static Mock<DbSet<T>> BuildSet<T>(List<T> rows) where T : class
        {
            var data = rows.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(() => new TestAsyncEnumerator<T>(rows.GetEnumerator()));
            set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => rows.GetEnumerator());
            return set;
        }
    }
}
