using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Reports work without a request that ran in a scope nobody marked as system work (<see cref="ISystemWorkScope"/>) and
/// so got no project access, at most once per <see cref="Interval"/> for the process, so a busy job cannot flood the
/// log.
/// </summary>
internal sealed class UnmarkedSystemWorkLog(TimeProvider timeProvider, ILogger<UnmarkedSystemWorkLog> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private DateTimeOffset? _lastReported;

    public void Report()
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastReported != null && now - _lastReported.Value < Interval)
            {
                return;
            }

            _lastReported = now;
        }

        logger.LogWarning(
            "Work without a request ran in a scope not marked as system work; it gets no project access. "
            + "Job hosts and startup tasks must mark their scope with ISystemWorkScope.");
    }
}
