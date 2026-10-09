using Beacon.Core.Data;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>
/// A <see cref="BeaconContext"/> for service tests without a database (§4.7: no in-memory provider). The sets in
/// <c>sets</c> are served from in-memory lists that support EF's async operators; every other set is the real EF
/// set, which only tracks. SaveChanges records the added entities in <c>saved</c> instead of writing them, and
/// changes to served entities land on the list's objects, so later contexts see them.
/// </summary>
internal sealed class RecordingBeaconContext(IReadOnlyDictionary<Type, object> sets, List<object> saved)
    : BeaconContext(Options, "beacon")
{
    private static readonly DbContextOptions<RecordingBeaconContext> Options =
        new DbContextOptionsBuilder<RecordingBeaconContext>()
            .UseNpgsql("Host=localhost;Database=unused")
            .UseSnakeCaseNamingConvention()
            .Options;

    public override DbSet<TEntity> Set<TEntity>() where TEntity : class =>
        sets.TryGetValue(typeof(TEntity), out var set) ? (DbSet<TEntity>)set : base.Set<TEntity>();

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var added = ChangeTracker.Entries()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .ToList();
        saved.AddRange(added);
        return Task.FromResult(added.Count);
    }

    /// <summary>An async-capable set over <paramref name="rows"/>; <c>Add</c> records into <paramref name="saved"/>.</summary>
    public static DbSet<T> MemorySet<T>(List<T> rows, List<object> saved) where T : class
    {
        var data = rows.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(data.GetEnumerator()));
        set.As<IQueryable<T>>()
            .Setup(x => x.Provider)
            .Returns(new TestAsyncQueryProvider<T>(data.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => data.GetEnumerator());
        set.Setup(x => x.Add(It.IsAny<T>())).Callback<T>(saved.Add);
        return set.Object;
    }
}
