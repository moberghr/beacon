using System.Collections.Concurrent;

namespace Beacon.Core.Services.Security;

public class RateLimiter
{
    private const int SweepEveryCalls = 1000;
    private static readonly TimeSpan MinSweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new();
    private int _callsSinceSweep;
    private long _lastSweepTicks = DateTime.UtcNow.Ticks;

    public bool IsAllowed(string key, int maxRequests, TimeSpan window)
    {
        var slidingWindow = _windows.GetOrAdd(key, _ => new SlidingWindow(maxRequests, window));
        var allowed = slidingWindow.TryAcquire();
        MaybeSweep();
        return allowed;
    }

    public RateLimitInfo GetInfo(string key, int maxRequests, TimeSpan window)
    {
        var slidingWindow = _windows.GetOrAdd(key, _ => new SlidingWindow(maxRequests, window));
        return slidingWindow.GetInfo();
    }

    // Opportunistic eviction so the dictionary does not grow without bound. Triggers every
    // SweepEveryCalls acquisitions, throttled to at most once per MinSweepInterval. Only fully
    // expired windows are removed via TryRemove(KeyValuePair) so an entry that received a fresh
    // timestamp between the check and the removal is left intact.
    private void MaybeSweep()
    {
        if (Interlocked.Increment(ref _callsSinceSweep) < SweepEveryCalls)
        {
            return;
        }

        var lastSweepTicks = Interlocked.Read(ref _lastSweepTicks);
        var now = DateTime.UtcNow;
        if (now - new DateTime(lastSweepTicks, DateTimeKind.Utc) < MinSweepInterval)
        {
            return;
        }

        // Claim the sweep; if another thread already advanced the timestamp, let it run instead.
        if (Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, lastSweepTicks) != lastSweepTicks)
        {
            return;
        }

        Interlocked.Exchange(ref _callsSinceSweep, 0);

        foreach (var entry in _windows)
        {
            if (entry.Value.IsExpired())
            {
                ((System.Collections.Generic.ICollection<KeyValuePair<string, SlidingWindow>>)_windows).Remove(entry);
            }
        }
    }

    private sealed class SlidingWindow(int maxRequests, TimeSpan window)
    {
        private readonly Queue<DateTime> _timestamps = new();
        private readonly object _lock = new();

        public bool TryAcquire()
        {
            lock (_lock)
            {
                Cleanup();
                if (_timestamps.Count >= maxRequests)
                    return false;
                _timestamps.Enqueue(DateTime.UtcNow);
                return true;
            }
        }

        public RateLimitInfo GetInfo()
        {
            lock (_lock)
            {
                Cleanup();
                var remaining = Math.Max(0, maxRequests - _timestamps.Count);
                var resetAt = _timestamps.Count > 0 ? _timestamps.Peek().Add(window) : DateTime.UtcNow;
                return new RateLimitInfo(remaining, maxRequests, resetAt);
            }
        }

        public bool IsExpired()
        {
            lock (_lock)
            {
                Cleanup();
                return _timestamps.Count == 0;
            }
        }

        private void Cleanup()
        {
            var cutoff = DateTime.UtcNow - window;
            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                _timestamps.Dequeue();
        }
    }
}

public record RateLimitInfo(int Remaining, int Limit, DateTime ResetsAt);
