using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// The stored catalog of a data source (its tables, columns, descriptions and stored sample values) as the ask path reads
/// it, cached per process for a short time. Every write that changes the stored catalog calls <see cref="Invalidate"/> after
/// its change is saved: a metadata refresh, a host model sync, an API endpoint import, a description patch, a data-source
/// update, enable, disable or delete, a migration-write mark (§6.2 pairs a cache with its invalidation).
/// </summary>
public interface ICatalogSnapshotCache
{
    /// <summary>Drops every cached snapshot of <paramref name="dataSourceId"/>, in every project; the next read loads it again.</summary>
    void Invalidate(int dataSourceId);
}

/// <summary>A cache that holds nothing: what a writer built without the host's cache invalidates.</summary>
internal sealed class NoCatalogSnapshotCache : ICatalogSnapshotCache
{
    public static readonly NoCatalogSnapshotCache Instance = new();

    public void Invalidate(int dataSourceId)
    {
    }
}

/// <summary>
/// Snapshots are keyed by project and data source, and read only after the caller's live check that its project links the
/// source and may read it (a removed link, a disabled source or a write target is refused before the cache is reached).
/// What depends on the project's settings (the stored-sample screen, the probe budget) is applied to the snapshot on every
/// read, never cached. An entry expires after <see cref="Expiration"/>, which bounds how long another process serves a
/// snapshot this process invalidated. The cache is held to <see cref="SizeLimit"/> units (one per table and column); a
/// snapshot larger than that is not cached.
/// </summary>
internal sealed class CatalogSnapshotCache : ICatalogSnapshotCache, IDisposable
{
    internal const long SizeLimit = 200_000;
    internal static readonly TimeSpan Expiration = TimeSpan.FromMinutes(15);

    private readonly MemoryCache _cache;

    // A data source's generation goes up on every invalidation. A snapshot is stored with the generation read before its
    // load began and served only while that is still the current one, so a load that overlapped a write is never served.
    private readonly ConcurrentDictionary<int, long> _generations = new();

    public CatalogSnapshotCache(TimeProvider? timeProvider = null)
    {
        var clock = timeProvider ?? TimeProvider.System;
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = SizeLimit,
            Clock = new TimeProviderClock(clock)
        });
    }

    /// <summary>
    /// The cached snapshot of <paramref name="dataSourceId"/> for <paramref name="projectId"/>, or the one
    /// <paramref name="load"/> reads, which is cached. The caller has checked the project's access to the source first.
    /// </summary>
    internal async Task<T> GetOrLoadAsync<T>(
        int projectId,
        int dataSourceId,
        Func<CancellationToken, Task<T>> load,
        Func<T, long> sizeOf,
        CancellationToken cancellationToken)
        where T : class
    {
        var key = new SnapshotKey(typeof(T), projectId, dataSourceId);
        var generation = Generation(dataSourceId);
        if (_cache.TryGetValue(key, out Entry? cached) && cached != null)
        {
            if (cached.Generation == generation)
            {
                return (T)cached.Value;
            }

            _cache.Remove(key);
        }

        var loaded = await load(cancellationToken);
        var size = Math.Max(1, sizeOf(loaded));
        if (size <= SizeLimit)
        {
            _cache.Set(key, new Entry(loaded, generation), new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Expiration,
                Size = size
            });
        }

        return loaded;
    }

    public void Invalidate(int dataSourceId)
    {
        _generations.AddOrUpdate(dataSourceId, 1, (_, x) => x + 1);
    }

    public void Dispose()
    {
        _cache.Dispose();
    }

    private long Generation(int dataSourceId)
    {
        return _generations.TryGetValue(dataSourceId, out var generation) ? generation : 0;
    }

    private sealed record SnapshotKey(Type Kind, int ProjectId, int DataSourceId);

    private sealed record Entry(object Value, long Generation);

#pragma warning disable CS0618 // ISystemClock is the clock MemoryCacheOptions takes.
    private sealed class TimeProviderClock(TimeProvider timeProvider) : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow => timeProvider.GetUtcNow();
    }
#pragma warning restore CS0618
}
