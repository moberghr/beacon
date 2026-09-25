using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Metadata;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Unit.HostData;

/// <summary>
/// BeaconContext whose sets are list-backed async doubles (no database, no UseInMemoryDatabase). The lists
/// outlive each context instance so a test can run the synchronizer twice against the same "database".
/// SaveChanges assigns ids and wires the navigation-created rows the way EF would.
/// </summary>
internal sealed class HostSyncStore
{
    public List<DataSource> DataSources { get; } = [];
    public List<Project> Projects { get; } = [];
    public List<ProjectDataSource> ProjectDataSources { get; } = [];
    public List<DatabaseMetadata> Tables { get; } = [];
    public List<ColumnMetadata> RemovedColumns { get; } = [];
    public List<IndexMetadata> RemovedIndexes { get; } = [];
    public int SaveCount { get; set; }
    private int _nextId = 100;

    public int NextId() => _nextId++;
}

internal sealed class HostSyncTestContext(HostSyncStore store) : BeaconContext(Options, "beacon")
{
    private static readonly DbContextOptions<HostSyncTestContext> Options =
        new DbContextOptionsBuilder<HostSyncTestContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

    public override DbSet<TEntity> Set<TEntity>()
    {
        object? set = typeof(TEntity).Name switch
        {
            nameof(DataSource) => ListSet(store.DataSources, null),
            nameof(Project) => ListSet(store.Projects, null),
            nameof(ProjectDataSource) => ListSet(store.ProjectDataSources, null),
            nameof(DatabaseMetadata) => ListSet(store.Tables, null),
            nameof(ColumnMetadata) => ListSet(new List<ColumnMetadata>(), store.RemovedColumns),
            nameof(IndexMetadata) => ListSet(new List<IndexMetadata>(), store.RemovedIndexes),
            _ => null
        };

        return set as DbSet<TEntity> ?? base.Set<TEntity>();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        store.SaveCount++;

        foreach (var dataSource in store.DataSources.Where(x => x.Id == 0))
        {
            dataSource.Id = store.NextId();
        }

        foreach (var project in store.Projects.Where(x => x.Id == 0))
        {
            project.Id = store.NextId();
        }

        foreach (var link in store.ProjectDataSources)
        {
            link.ProjectId = link.Project?.Id ?? link.ProjectId;
            link.DataSourceId = link.DataSource?.Id ?? link.DataSourceId;
        }

        foreach (var table in store.Tables)
        {
            if (table.Id == 0)
            {
                table.Id = store.NextId();
            }

            table.DataSourceId = table.DataSource?.Id ?? table.DataSourceId;
        }

        return Task.FromResult(1);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DbSet<T> ListSet<T>(List<T> data, List<T>? removed) where T : class
    {
        var queryable = data.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.ToList().GetEnumerator()));
        set.As<IQueryable<T>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<T>(queryable.Provider));
        set.As<IQueryable<T>>()
            .Setup(x => x.Expression)
            .Returns(queryable.Expression);
        set.As<IQueryable<T>>()
            .Setup(x => x.ElementType)
            .Returns(queryable.ElementType);
        set.As<IQueryable<T>>()
            .Setup(x => x.GetEnumerator())
            .Returns(() => data.ToList().GetEnumerator());
        set.Setup(x => x.Add(It.IsAny<T>()))
            .Callback<T>(data.Add)
            .Returns((Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry<T>)null!);
        set.Setup(x => x.AddRange(It.IsAny<IEnumerable<T>>()))
            .Callback<IEnumerable<T>>(data.AddRange);
        set.Setup(x => x.RemoveRange(It.IsAny<IEnumerable<T>>()))
            .Callback<IEnumerable<T>>(x => (removed ?? []).AddRange(x));

        return set.Object;
    }
}
