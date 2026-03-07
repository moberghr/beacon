using System.Collections.Concurrent;

namespace Semantico.Core.Services.Security;

public class RateLimiter
{
    private readonly ConcurrentDictionary<string, SlidingWindow> _windows = new();

    public bool IsAllowed(string key, int maxRequests, TimeSpan window)
    {
        var slidingWindow = _windows.GetOrAdd(key, _ => new SlidingWindow(maxRequests, window));
        return slidingWindow.TryAcquire();
    }

    public RateLimitInfo GetInfo(string key, int maxRequests, TimeSpan window)
    {
        var slidingWindow = _windows.GetOrAdd(key, _ => new SlidingWindow(maxRequests, window));
        return slidingWindow.GetInfo();
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

        private void Cleanup()
        {
            var cutoff = DateTime.UtcNow - window;
            while (_timestamps.Count > 0 && _timestamps.Peek() < cutoff)
                _timestamps.Dequeue();
        }
    }
}

public record RateLimitInfo(int Remaining, int Limit, DateTime ResetsAt);
