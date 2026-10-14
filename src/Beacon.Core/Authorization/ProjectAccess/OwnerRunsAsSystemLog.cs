using Microsoft.Extensions.Logging;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// Warns that scheduled work of an owner without a stored user runs as the system (a host without Beacon user
/// management keeps no user to re-check, so the owner's access is not checked at all), at most once per
/// <see cref="Interval"/> for each owner, so a frequent schedule cannot flood the log. Owners are named by id only.
/// </summary>
internal sealed class OwnerRunsAsSystemLog(TimeProvider timeProvider, ILogger<OwnerRunsAsSystemLog> logger)
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    // Bounds the memory a host with very many owners can take; past it, owners not reported within the interval are
    // forgotten first.
    private const int MaxTrackedOwners = 1000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastReported = new(StringComparer.Ordinal);

    public void Report(string ownerId)
    {
        var now = timeProvider.GetUtcNow();
        lock (_gate)
        {
            if (_lastReported.TryGetValue(ownerId, out var last) && now - last < Interval)
            {
                return;
            }

            if (_lastReported.Count >= MaxTrackedOwners)
            {
                Forget(now);
            }

            _lastReported[ownerId] = now;
        }

        logger.LogWarning(
            "Scheduled work of owner {OwnerId} runs as the system: a host without Beacon user management keeps no user for the owner, so the owner's access is not re-checked",
            ownerId);
    }

    private void Forget(DateTimeOffset now)
    {
        var expired = _lastReported
            .Where(x => now - x.Value >= Interval)
            .Select(x => x.Key)
            .ToList();
        foreach (var ownerId in expired)
        {
            _lastReported.Remove(ownerId);
        }

        if (_lastReported.Count >= MaxTrackedOwners)
        {
            _lastReported.Clear();
        }
    }
}
