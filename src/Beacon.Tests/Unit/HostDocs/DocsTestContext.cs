using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Unit.HostDocs;

/// <summary>
/// The "database" behind <see cref="DocsTestContext"/>: one list per entity type, outliving each context instance so
/// a test can run the synchronizer repeatedly. Any entity type a test does not seed is an empty list.
/// </summary>
internal sealed class DocsStore
{
    private readonly Dictionary<Type, object> _lists = [];
    private int _nextId = 1000;

    public int SaveCount { get; set; }

    /// <summary>When set, the next SaveChanges throws this (e.g. a unique violation from a lost race) and clears it.</summary>
    public Exception? FailNextSave { get; set; }

    public List<Project> Projects => ListFor<Project>();

    public List<ProjectImportedDocument> Documents => ListFor<ProjectImportedDocument>();

    public List<McpDocChunk> Chunks => ListFor<McpDocChunk>();

    public List<McpEmbedding> Embeddings => ListFor<McpEmbedding>();

    public List<T> ListFor<T>()
    {
        if (!_lists.TryGetValue(typeof(T), out var list))
        {
            list = new List<T>();
            _lists[typeof(T)] = list;
        }

        return (List<T>)list;
    }

    public int NextId() => _nextId++;

    public Mock<IDbContextFactory<BeaconContext>> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new DocsTestContext(this));

        return factory;
    }
}

/// <summary>
/// BeaconContext over list-backed async doubles (no database, no UseInMemoryDatabase — §4.7). Add/AddRange append,
/// Remove/RemoveRange delete, and SaveChanges assigns ids and fixes up the chunk → document foreign key the way EF
/// would. The global soft-delete filter is NOT applied in memory; tests filter archived rows explicitly.
/// </summary>
internal sealed class DocsTestContext(DocsStore store) : BeaconContext(Options, "beacon")
{
    private static readonly DbContextOptions<DocsTestContext> Options =
        new DbContextOptionsBuilder<DocsTestContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

    public override DbSet<TEntity> Set<TEntity>()
    {
        return ListSet(store.ListFor<TEntity>());
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        store.SaveCount++;

        if (store.FailNextSave != null)
        {
            var failure = store.FailNextSave;
            store.FailNextSave = null;
            throw failure;
        }

        foreach (var project in store.Projects.Where(x => x.Id == 0))
        {
            project.Id = store.NextId();
        }

        foreach (var document in store.Documents.Where(x => x.Id == 0))
        {
            document.Id = store.NextId();
        }

        foreach (var chunk in store.Chunks)
        {
            if (chunk.Id == 0)
            {
                chunk.Id = store.NextId();
            }

            chunk.ImportedDocumentId = chunk.ImportedDocument?.Id ?? chunk.ImportedDocumentId;
        }

        foreach (var embedding in store.Embeddings.Where(x => x.Id == 0))
        {
            embedding.Id = store.NextId();
        }

        return Task.FromResult(1);
    }

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static DbSet<T> ListSet<T>(List<T> data) where T : class
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
        set.Setup(x => x.AddRangeAsync(It.IsAny<IEnumerable<T>>(), It.IsAny<CancellationToken>()))
            .Callback<IEnumerable<T>, CancellationToken>((x, _) => data.AddRange(x))
            .Returns(Task.CompletedTask);
        set.Setup(x => x.RemoveRange(It.IsAny<IEnumerable<T>>()))
            .Callback<IEnumerable<T>>(x =>
            {
                foreach (var item in x.ToList())
                {
                    data.Remove(item);
                }
            });

        return set.Object;
    }
}
