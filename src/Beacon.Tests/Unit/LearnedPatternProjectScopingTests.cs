using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.AI.Services.Documentation;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.LlmProviders;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Providers;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC9 (§1.12) — two projects share data source 1; learned patterns are grounding material and must be
/// filtered by the caller's project, not just by data source. Both learned-pattern read sites in
/// <c>ProjectDocumentationService</c> are driven through <c>TestAsyncQueryable</c>-backed DbSets (§4.7).
/// </summary>
[TestFixture]
public class LearnedPatternProjectScopingTests
{
    private const int ProjectA = 1;
    private const int ProjectB = 2;
    private const int SharedDataSourceId = 1;
    private const string PatternA = "pattern-owned-by-project-a";
    private const string PatternB = "pattern-owned-by-project-b";

    [Test]
    public async Task ExportToMarkdown_ForProjectB_ContainsOnlyProjectBPatterns()
    {
        var service = BuildService();

        var markdown = await service.ExportToMarkdownAsync(documentationId: 20, CancellationToken.None);

        markdown.Should().Contain(PatternB);
        markdown.Should().NotContain(PatternA);
    }

    [Test]
    public async Task GetLearnedPatternsForDoc_ForProjectB_ReturnsOnlyProjectBPatterns()
    {
        var service = BuildService();

        var patterns = await service.GetLearnedPatternsForDocAsync(ProjectB, [SharedDataSourceId], CancellationToken.None);

        patterns.Select(x => x.Content).Should().Equal(PatternB);
    }

    [Test]
    public async Task GetLearnedPatternsForDoc_ForProjectA_ReturnsOnlyProjectAPatterns()
    {
        var service = BuildService();

        var patterns = await service.GetLearnedPatternsForDocAsync(ProjectA, [SharedDataSourceId], CancellationToken.None);

        patterns.Select(x => x.Content).Should().Equal(PatternA);
    }

    private static ProjectDocumentationService BuildService()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ScopingContext());

        return new ProjectDocumentationService(
            factory.Object,
            new Mock<IKnowledgeGraphService>().Object,
            new Mock<ILlmProvider>().Object,
            new Mock<IDataSourceProviderFactory>().Object,
            NullLogger<ProjectDocumentationService>.Instance);
    }

    private sealed class ScopingContext : BeaconContext
    {
        private static readonly DbContextOptions<ScopingContext> Options =
            new DbContextOptionsBuilder<ScopingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public ScopingContext() : base(Options, "beacon")
        {
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpLearnedPattern))
            {
                return (DbSet<TEntity>)(object)BuildSet(
                [
                    Pattern(ProjectA, PatternA),
                    Pattern(ProjectB, PatternB)
                ]);
            }

            if (typeof(TEntity) == typeof(ProjectDataSource))
            {
                return (DbSet<TEntity>)(object)BuildSet(
                [
                    new ProjectDataSource { ProjectId = ProjectA, DataSourceId = SharedDataSourceId },
                    new ProjectDataSource { ProjectId = ProjectB, DataSourceId = SharedDataSourceId }
                ]);
            }

            if (typeof(TEntity) == typeof(ProjectDocumentation))
            {
                return (DbSet<TEntity>)(object)BuildSet(
                [
                    new ProjectDocumentation
                    {
                        Id = 20,
                        ProjectId = ProjectB,
                        GeneratedByModel = "test-model",
                        Project = new Project { Id = ProjectB, Name = "Project B" }
                    }
                ]);
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);

        private static McpLearnedPattern Pattern(int projectId, string content)
        {
            return new McpLearnedPattern
            {
                ProjectId = projectId,
                DataSourceId = SharedDataSourceId,
                SchemaName = "public",
                TableName = "orders",
                PatternContent = content,
                Confidence = 0.9,
                Status = McpPatternStatus.Approved
            };
        }

        private static DbSet<T> BuildSet<T>(List<T> rows) where T : class
        {
            var data = rows.AsQueryable();
            var set = new Mock<DbSet<T>>();
            set.As<IAsyncEnumerable<T>>()
                .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
                .Returns(new TestAsyncEnumerator<T>(data.GetEnumerator()));
            set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
            set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
            set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
            set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(data.GetEnumerator());

            return set.Object;
        }
    }
}
