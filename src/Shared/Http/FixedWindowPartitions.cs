using System.Runtime.CompilerServices;
using Beacon.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Beacon.Shared.Http;

/// <summary>
/// One <see cref="RateLimitWindow"/> of <c>Beacon:RateLimits</c> as a partitioned fixed-window limiter: each partition (a
/// caller key) gets the window's permits, counted from its first request in the window. It does not depend on the host's
/// <c>AddRateLimiter</c> / <c>UseRateLimiter</c>: Beacon's endpoints apply it themselves, so a host's own rate-limiter
/// configuration neither enables nor breaks it. At most <see cref="RateLimitOptions.MaxPartitions"/> partitions are kept:
/// a new one beyond that replaces the one used least recently, so memory stays bounded whatever number of callers or
/// addresses send requests. A window with no permits is off and admits everything.
/// </summary>
internal sealed class FixedWindowPartitions : IDisposable
{
    private readonly RateLimitWindow _window;
    private readonly int _maxPartitions;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Partition>> _partitions = new(StringComparer.Ordinal);

    // Most recently used first; the last one is replaced when a new partition would exceed the bound.
    private readonly LinkedList<Partition> _recentlyUsed = new();

    public FixedWindowPartitions(RateLimitWindow window, int maxPartitions = RateLimitOptions.DefaultMaxPartitions, TimeProvider? timeProvider = null)
    {
        _window = window;
        _maxPartitions = Math.Max(1, maxPartitions);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The partitions kept now, at most the bound given at construction.</summary>
    public int PartitionCount
    {
        get
        {
            lock (_gate)
            {
                return _partitions.Count;
            }
        }
    }

    /// <summary>
    /// Takes one permit for <paramref name="partition"/>. False when the partition has none left; <paramref name="retryAfter"/>
    /// is then how long until its window renews (at least one second).
    /// </summary>
    public bool TryAcquire(string partition, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_window.IsEnabled)
        {
            return true;
        }

        var now = _timeProvider.GetUtcNow();
        lock (_gate)
        {
            var entry = Touch(partition, now);
            if (now >= entry.WindowStart + _window.Window)
            {
                entry.WindowStart = now;
                entry.Count = 0;
            }

            if (entry.Count < _window.PermitLimit)
            {
                entry.Count++;

                return true;
            }

            var wait = entry.WindowStart + _window.Window - now;
            retryAfter = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;

            return false;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _partitions.Clear();
            _recentlyUsed.Clear();
        }
    }

    // The partition's entry, moved to the front of the recently used list; a new one replaces the least recently used
    // entry when the bound is reached. Called under the lock.
    private Partition Touch(string key, DateTimeOffset now)
    {
        if (_partitions.TryGetValue(key, out var node))
        {
            _recentlyUsed.Remove(node);
            _recentlyUsed.AddFirst(node);

            return node.Value;
        }

        if (_partitions.Count >= _maxPartitions)
        {
            var oldest = _recentlyUsed.Last!;
            _recentlyUsed.RemoveLast();
            _partitions.Remove(oldest.Value.Key);
        }

        var created = _recentlyUsed.AddFirst(new Partition(key) { WindowStart = now });
        _partitions[key] = created;

        return created.Value;
    }

    private sealed class Partition(string key)
    {
        public string Key { get; } = key;

        public DateTimeOffset WindowStart { get; set; }

        public int Count { get; set; }
    }
}

/// <summary>
/// The limiter that stands in for a <typeparamref name="TLimiter"/> the host did not register, so Beacon's limits never
/// fail open: one per application, built from the <c>Beacon:RateLimits</c> options the application registered (or the
/// defaults). The application is told by its root <see cref="IServiceScopeFactory"/>, the one every request scope of a
/// container resolves, so two hosts in one process never share counts. Code that runs without any service provider gets
/// one limiter of its own kind.
/// </summary>
internal static class UnregisteredLimiter<TLimiter>
    where TLimiter : class
{
    private static readonly ConditionalWeakTable<object, TLimiter> PerApplication = new();
    private static TLimiter? _withoutServices;

    public static TLimiter For(IServiceProvider? services, Func<IOptions<RateLimitOptions>, TLimiter> create)
    {
        var application = services?.GetService<IServiceScopeFactory>();
        if (application == null)
        {
            return LazyInitializer.EnsureInitialized(ref _withoutServices, () => create(OptionsOf(services)));
        }

        return PerApplication.GetValue(application, _ => create(OptionsOf(services)));
    }

    private static IOptions<RateLimitOptions> OptionsOf(IServiceProvider? services)
    {
        return services?.GetService<IOptions<RateLimitOptions>>() ?? Options.Create(new RateLimitOptions());
    }
}
