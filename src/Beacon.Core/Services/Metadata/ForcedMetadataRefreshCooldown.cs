using Microsoft.Extensions.Caching.Memory;
using Beacon.Core.Configuration;

namespace Beacon.Core.Services.Metadata;

/// <summary>
/// The forced metadata refresh cooldown (<see cref="MetadataRefreshOptions.ForcedRefreshCooldownMinutes"/>), per data
/// source and per Beacon process. The time of the last forced refresh that succeeded is kept in the memory cache, so a
/// refresh that fails (a broken connection, a cancelled request) does not hold back its retry, and the cooldown in force
/// when the next request arrives is the one applied. Changing or archiving the data source clears it
/// (<see cref="CacheKey"/>). While the cooldown is on, one forced refresh of a data source runs at a time, also for a
/// caller the cooldown itself does not hold back (an Admin, a DataSourceAdmin).
/// </summary>
internal sealed class ForcedMetadataRefreshCooldown(IMemoryCache cache, TimeProvider timeProvider)
{
    public const string AlreadyRunningMessage =
        "A metadata refresh of this data source is already running; try again when it has finished.";

    private readonly Lock _gate = new();
    private readonly HashSet<int> _running = [];

    /// <summary>The memory-cache key holding the time of the data source's last successful forced refresh.</summary>
    public static string CacheKey(int dataSourceId) => BeaconCacheEntries.Key($"metadata-forced-refresh:{dataSourceId}");

    /// <summary>
    /// Why a forced refresh of the data source may not start now (it runs in this process, or the cooldown holds it back),
    /// or null when it may; unlike <see cref="TryStart"/> it starts nothing. A cooldown of 0 never refuses.
    /// </summary>
    public string? Refusal(int dataSourceId, int cooldownMinutes, bool ignoreCooldown)
    {
        if (cooldownMinutes <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            return RefusalWithinGate(dataSourceId, cooldownMinutes, ignoreCooldown);
        }
    }

    /// <summary>
    /// Starts a forced refresh of the data source: null when it may run (it is then running until
    /// <see cref="Finish"/>), else why not. A cooldown of 0 never refuses; <paramref name="ignoreCooldown"/> refuses only
    /// while another refresh of the data source runs.
    /// </summary>
    public string? TryStart(int dataSourceId, int cooldownMinutes, bool ignoreCooldown)
    {
        if (cooldownMinutes <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            var refusal = RefusalWithinGate(dataSourceId, cooldownMinutes, ignoreCooldown);
            if (refusal != null)
            {
                return refusal;
            }

            _running.Add(dataSourceId);
            return null;
        }
    }

    /// <summary>Ends a refresh <see cref="TryStart"/> started; a successful one starts the cooldown.</summary>
    public void Finish(int dataSourceId, int cooldownMinutes, bool succeeded)
    {
        if (cooldownMinutes <= 0)
        {
            return;
        }

        if (succeeded)
        {
            // Kept for the longest cooldown accepted, so a setting raised later still sees it.
            cache.Set(CacheKey(dataSourceId), timeProvider.GetUtcNow(), BeaconCacheEntries.For(TimeSpan.FromMinutes(MetadataRefreshOptions.MaxCooldownMinutes)));
        }

        lock (_gate)
        {
            _running.Remove(dataSourceId);
        }
    }

    private string? RefusalWithinGate(int dataSourceId, int cooldownMinutes, bool ignoreCooldown)
    {
        if (_running.Contains(dataSourceId))
        {
            return AlreadyRunningMessage;
        }

        if (!ignoreCooldown
            && cache.TryGetValue(CacheKey(dataSourceId), out DateTimeOffset lastRefreshed)
            && timeProvider.GetUtcNow() - lastRefreshed < TimeSpan.FromMinutes(cooldownMinutes))
        {
            return $"This data source's metadata was refreshed less than {cooldownMinutes} minutes ago; try again later.";
        }

        return null;
    }
}
