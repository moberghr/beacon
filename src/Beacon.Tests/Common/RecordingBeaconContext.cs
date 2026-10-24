using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// A <see cref="BeaconContext"/> for service tests without a database (§4.7: no in-memory provider). The sets in
/// <c>sets</c> are served from in-memory lists that support EF's async operators; every other set is the real EF
/// set, which only tracks. SaveChanges records the added entities in <c>saved</c> instead of writing them, and
/// changes to served entities land on the list's objects, so later contexts see them. With an <c>offline</c> capture
/// it never opens a connection, and transactions are stand-ins. <c>onSave</c> is told about every SaveChanges, for tests
/// that count units of work. <c>interceptors</c> are registered on the context as a host registers them (for code that
/// checks which it runs with); without an <c>offline</c> capture only.
/// </summary>
internal sealed class RecordingBeaconContext(
    IReadOnlyDictionary<Type, object> sets,
    List<object> saved,
    SqlCapture? offline = null,
    Action? onSave = null,
    IInterceptor[]? interceptors = null)
    : BeaconContext(offline == null ? interceptors == null ? Options : WithInterceptors(interceptors) : OfflineOptions(offline), "beacon"), IObservableSaves
{
    // An unserved set is the real EF set: it never connects (SqlCapture.NeverConnects), so reading one fails at once.
    private static readonly DbContextOptions<RecordingBeaconContext> Options =
        new DbContextOptionsBuilder<RecordingBeaconContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(SqlCapture.NeverConnects)
            .Options;

    // For code that opens a transaction: the connection is never opened and the transaction is a stand-in the capture
    // records.
    private static DbContextOptions<RecordingBeaconContext> OfflineOptions(SqlCapture offline) =>
        new DbContextOptionsBuilder<RecordingBeaconContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(offline.OfflineInterceptors())
            .Options;

    private static DbContextOptions<RecordingBeaconContext> WithInterceptors(IInterceptor[] interceptors) =>
        new DbContextOptionsBuilder<RecordingBeaconContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .AddInterceptors([SqlCapture.NeverConnects, .. interceptors])
            .Options;

    /// <inheritdoc />
    public event Action? Saving;

    public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
        sets.TryGetValue(typeof(TEntity), out var set) ? (DbSet<TEntity>)set : base.Set<TEntity>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Saving?.Invoke();
        var added = ChangeTracker.Entries()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        saved.AddRange(added);
        onSave?.Invoke();
        return Task.FromResult(added.Count);
    }

    /// <summary>
    /// An async-capable set over <paramref name="rows"/>; <c>Add</c> records into <paramref name="saved"/>.
    /// <c>ExecuteUpdateAsync</c> writes onto the matching rows, and <c>ExecuteDeleteAsync</c>, <c>Remove</c> and <c>RemoveRange</c> take them
    /// out of <paramref name="rows"/>, as the database would. <c>FindAsync</c> finds a row by its <c>Id</c>. With
    /// <paramref name="reads"/>, queries (and the rows a bulk write matches) read through it instead: a view over
    /// <paramref name="rows"/> for a test that acts between two reads.
    /// </summary>
    public static DbSet<T> MemorySet<T>(List<T> rows, List<object> saved, IEnumerable<T>? reads = null) where T : class
    {
        var data = (reads ?? rows).AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<T>(data.Provider, matched =>
            {
                foreach (var row in matched)
                {
                    rows.Remove(row);
                }

                return matched.Count;
            }));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set.Setup(x => x.Add(It.IsAny<T>())).Callback<T>(saved.Add);
        set.Setup(x => x.Remove(It.IsAny<T>())).Callback<T>(x => rows.Remove(x));
        set.Setup(x => x.RemoveRange(It.IsAny<IEnumerable<T>>())).Callback<IEnumerable<T>>(x =>
        {
            foreach (var row in x.ToList())
            {
                rows.Remove(row);
            }
        });

        // As EF's own set: a query started with AsQueryable() is the set itself.
        set.Setup(x => x.AsQueryable()).Returns(() => set.Object);

        // FindAsync by the entity's Id, its primary key.
        var id = typeof(T).GetProperty("Id");
        if (id != null)
        {
            set.Setup(x => x.FindAsync(It.IsAny<object?[]?>(), It.IsAny<CancellationToken>()))
                .Returns((object?[]? keys, CancellationToken _) => ValueTask.FromResult<T?>(
                    rows.FirstOrDefault(x => keys is { Length: 1 } && Equals(id.GetValue(x), keys[0]))));
        }

        return set.Object;
    }
}
