using System.Threading.RateLimiting;
using Beacon.Core.Configuration;

namespace Beacon.Shared.Http;

/// <summary>
/// One <see cref="RateLimitWindow"/> of <c>Beacon:RateLimits</c> as a partitioned fixed-window limiter: each partition (a
/// caller key) gets the window's permits. It does not depend on the host's <c>AddRateLimiter</c> / <c>UseRateLimiter</c>:
/// Beacon's endpoints apply it themselves, so a host's own rate-limiter configuration neither enables nor breaks it. Idle
/// partitions are released by the limiter. A window with no permits is off and admits everything.
/// </summary>
internal sealed class FixedWindowPartitions : IDisposable
{
    private readonly RateLimitWindow _window;
    private readonly PartitionedRateLimiter<string> _limiter;

    public FixedWindowPartitions(RateLimitWindow window)
    {
        _window = window;
        _limiter = PartitionedRateLimiter.Create<string, string>(x =>
            RateLimitPartition.GetFixedWindowLimiter(
                x,
                _ =>
                    new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = Math.Max(1, window.PermitLimit),
                        Window = window.Window,
                        QueueLimit = 0,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        AutoReplenishment = true
                    }));
    }

    /// <summary>
    /// Takes one permit for <paramref name="partition"/>. False when the partition has none left; <paramref name="retryAfter"/>
    /// is then how long until the window renews (at least one second).
    /// </summary>
    public bool TryAcquire(string partition, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (!_window.IsEnabled)
        {
            return true;
        }

        using var lease = _limiter.AttemptAcquire(partition);
        if (lease.IsAcquired)
        {
            return true;
        }

        var wait = lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : _window.Window;
        retryAfter = wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait;

        return false;
    }

    public void Dispose() => _limiter.Dispose();
}
