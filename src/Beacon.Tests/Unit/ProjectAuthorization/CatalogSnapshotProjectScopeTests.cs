using System.Collections;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
using Beacon.Core.Models;
using Beacon.Core.Models.Metadata;
using Beacon.Core.Services.Metadata;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The ask path reads a data source's stored catalog from a snapshot cached per project and data source (§1.12, §6.2). The
/// project's link is checked live on every ask, before the snapshot: a project that does not link the source, a removed
/// link and a disabled source are refused although another ask cached the snapshot. Stored sample values are screened
/// under each project's own settings. An invalidation makes the next ask read the changed catalog. Nothing reaches a
/// database (§4.7).
/// </summary>
[TestFixture]
public class CatalogSnapshotProjectScopeTests
{
    private const int DataSourceId = 7;
    private const int CallerProjectId = 41;
    private const int OtherProjectId = 42;
    private const string Question = "how many orders";

    [Test]
    public async Task ASecondAsk_ReadsTheStoredCatalogFromTheSnapshot()
    {
        var world = new World();
        var service = world.Service();

        var first = await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var second = await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var catalog = await service.GetSchemaCatalogAsync(DataSourceId, CallerProjectId);

        world.CatalogReads.Should().Be(1, "the second ask and the schema catalog are served from the snapshot");
        second.FullContext.Should().Be(first.FullContext);
        catalog.Should().ContainKey("sales.orders");
    }

    [Test]
    public async Task AfterAnInvalidation_TheNextAskReadsTheChangedCatalog()
    {
        var world = new World();
        var service = world.Service();
        await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);

        world.Orders.TableDescription = "Orders, curated";
        var beforeInvalidation = await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);
        world.Snapshots.Invalidate(DataSourceId);
        var afterInvalidation = await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);

        beforeInvalidation.FullContext.Should().NotContain("Orders, curated");
        afterInvalidation.FullContext.Should().Contain("Orders, curated");
        world.CatalogReads.Should().Be(2);
    }

    [Test]
    public async Task AProjectThatDoesNotLinkTheSource_IsRefused_ThoughAnotherProjectCachedItsSnapshot()
    {
        var world = new World();
        var service = world.Service();
        await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);

        var ask = () => service.GetSmartContextForAskAsync(DataSourceId, OtherProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var catalog = await service.GetSchemaCatalogAsync(DataSourceId, OtherProjectId);

        await ask.Should().ThrowAsync<InvalidOperationException>().WithMessage($"Data source {DataSourceId} not found");
        catalog.Should().BeEmpty();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ARemovedLinkOrADisabledSource_IsRefused_ThoughItsSnapshotIsCached(bool disable)
    {
        var world = new World();
        var service = world.Service();
        await service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);

        if (disable)
        {
            world.Source.DisabledTime = DateTime.UtcNow;
        }
        else
        {
            world.Links.Clear();
        }

        var ask = () => service.GetSmartContextForAskAsync(DataSourceId, CallerProjectId, Question, liveValueProbes: true, CancellationToken.None);
        var catalog = await service.GetSchemaCatalogAsync(DataSourceId, CallerProjectId);

        await ask.Should().ThrowAsync<InvalidOperationException>();
        catalog.Should().BeEmpty();
    }

    // Two projects link the source; only the caller's collects sample values. Whichever asks first, the other project's
    // context never carries the stored samples the caller's shows.
    [TestCase(true)]
    [TestCase(false)]
    public async Task ProjectsSharingASource_EachScreenTheStoredSamplesUnderTheirOwnSettings(bool callerFirst)
    {
        var world = new World();
        world.Links.Add(new ProjectDataSource { ProjectId = OtherProjectId, DataSourceId = DataSourceId, DataSource = world.Source });
        var service = world.Service();

        var order = callerFirst ? new[] { CallerProjectId, OtherProjectId } : [OtherProjectId, CallerProjectId];
        var contexts = new Dictionary<int, SmartSchemaContext>();
        foreach (var projectId in order)
        {
            contexts[projectId] = await service.GetSmartContextForAskAsync(DataSourceId, projectId, Question, liveValueProbes: true, CancellationToken.None);
        }

        contexts[CallerProjectId].FullContext.Should().Contain("SHIPPED");
        contexts[OtherProjectId].FullContext.Should().NotContain("SHIPPED");
        world.CatalogReads.Should().Be(2, "each project reads its own snapshot");
    }

    private static readonly SchemaGraph EmptyGraph = SchemaGraph.Build(new List<TableMetadataDto>(), []);

    private sealed class World
    {
        public World()
        {
            Source = new DataSource
            {
                Id = DataSourceId,
                Name = "Sales DB",
                DataSourceType = DataSourceType.Database,
                DatabaseEngineType = DatabaseEngineType.PostgreSQL,
                EncryptedConnectionData = "unused-in-test"
            };
            Orders = new DatabaseMetadata { Id = 1, DataSourceId = DataSourceId, SchemaName = "sales", TableName = "orders", TableDescription = "Orders" };
            Orders.Columns =
            [
                new ColumnMetadata { Id = 10, ColumnName = "id", DataType = "integer", IsPrimaryKey = true, DatabaseMetadata = Orders },
                new ColumnMetadata { Id = 11, ColumnName = "status", DataType = "text", IsNullable = true, SampleValues = "[\"SHIPPED\"]", DatabaseMetadata = Orders }
            ];
            Links.Add(new ProjectDataSource { ProjectId = CallerProjectId, DataSourceId = DataSourceId, DataSource = Source });
            _metadata = new CountingList<DatabaseMetadata>([Orders], () => CatalogReads++);
        }

        private readonly CountingList<DatabaseMetadata> _metadata;

        public DataSource Source { get; }

        public DatabaseMetadata Orders { get; }

        public List<ProjectDataSource> Links { get; } = [];

        public int CatalogReads { get; private set; }

        public CatalogSnapshotCache Snapshots { get; } = new();

        public KnowledgeGraphService Service()
        {
            var settingsProvider = SettingsProviderMock.Create(
                new McpSettingsData(),
                projectSettings: new Dictionary<int, McpSettingsData>
                {
                    [CallerProjectId] = new() { EnableSampleValueCollection = true, EnableValueGrounding = false, EnableLearning = false },
                    [OtherProjectId] = new() { EnableSampleValueCollection = false, EnableValueGrounding = false, EnableLearning = false }
                });
            var factory = new Mock<IDbContextFactory<BeaconContext>>();
            factory
                .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => new SnapshotTestContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(DataSource)] = BuildDbSet(new List<DataSource> { Source }).Object,
                        [typeof(ProjectDataSource)] = BuildDbSet(Links).Object,
                        [typeof(DatabaseMetadata)] = BuildDbSet(_metadata).Object
                    }));

            return new KnowledgeGraphService(
                factory.Object,
                settingsProvider.Object,
                SettingsProviderMock.Unshared(settingsProvider.Object),
                new UnavailableEmbeddingService(),
                Mock.Of<ISchemaGraphService>(x => x.GetGraphAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()) == Task.FromResult(EmptyGraph)),
                Mock.Of<IValueGroundingService>(),
                TestConfiguredDatabases.None,
                new QueryGuardrailService(),
                NullLogger<KnowledgeGraphService>.Instance,
                catalogSnapshots: Snapshots);
        }
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

    // A list that counts how often it is read, each read being one load of the stored catalog.
    private sealed class CountingList<T>(List<T> items, Action onRead) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator()
        {
            onRead();

            return items.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
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
        set.Setup(x => x.AsQueryable()).Returns(() => set.Object);
        return set;
    }

    private sealed class SnapshotTestContext(Dictionary<Type, object> sets) : BeaconContext(Options, "beacon")
    {
        private static readonly DbContextOptions<SnapshotTestContext> Options =
            new DbContextOptionsBuilder<SnapshotTestContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            return sets.TryGetValue(typeof(TEntity), out var set)
                ? (DbSet<TEntity>)set
                : base.Set<TEntity>();
        }
    }
}
