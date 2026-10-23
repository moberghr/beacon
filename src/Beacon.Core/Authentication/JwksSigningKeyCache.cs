using System.Collections.Concurrent;
using Microsoft.IdentityModel.Tokens;

namespace Beacon.Core.Authentication;

/// <summary>
/// Caches the signing keys of a JWKS endpoint so bearer validation does not fetch the key set on every request.
/// Keys are refreshed after <see cref="RefreshInterval"/>; a token signed with an unknown key id may force an early
/// refresh (key rotation), but at most once per <see cref="MinimumForcedRefreshInterval"/> so a flood of forged tokens
/// cannot turn Beacon into a JWKS hammer. When a refresh fails and keys are already cached, the cached keys are kept, but
/// for at most <see cref="MaximumStaleness"/> after the last successful fetch: after that they are dropped and every
/// validation fails as keys-unavailable until a fetch succeeds, so a key the identity provider withdrew is not trusted
/// indefinitely while its JWKS cannot be read. A fetch that fails with no usable keys is not repeated for
/// <see cref="FailureRetryInterval"/>; the requests in between fail at once instead of queueing behind another fetch.
/// </summary>
internal sealed class JwksSigningKeyCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan MinimumForcedRefreshInterval = TimeSpan.FromMinutes(5);

    /// <summary>How long after the last successful fetch cached keys may still be used while refreshes fail.</summary>
    public static readonly TimeSpan MaximumStaleness = TimeSpan.FromHours(24);

    /// <summary>How long a fetch that left no usable keys is not repeated.</summary>
    public static readonly TimeSpan FailureRetryInterval = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The process-wide cache the provider uses when none is supplied.</summary>
    public static JwksSigningKeyCache Shared { get; } = new(TimeProvider.System);

    /// <summary>
    /// The endpoint's signing keys, fetched through <paramref name="fetchDocument"/> when nothing is cached, the cache
    /// is older than <see cref="RefreshInterval"/>, or <paramref name="forceRefresh"/> is set and the last fetch is
    /// older than <see cref="MinimumForcedRefreshInterval"/>. Throws when no usable keys can be had: the fetch failed and
    /// nothing is cached, the cached keys are older than <see cref="MaximumStaleness"/>, or a fetch failed like that less
    /// than <see cref="FailureRetryInterval"/> ago (<see cref="RetryNotDueException"/>, without fetching).
    /// </summary>
    public async Task<IReadOnlyList<SecurityKey>> GetKeysAsync(
        string endpoint,
        Func<CancellationToken, Task<string>> fetchDocument,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var entry = _entries.GetOrAdd(endpoint, _ => new Entry());
        var now = timeProvider.GetUtcNow();
        if (!NeedsFetch(entry, now, forceRefresh))
        {
            return entry.Keys!;
        }

        ThrowWhileBackingOff(entry, now);

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed (or failed to) while this one waited.
            now = timeProvider.GetUtcNow();
            if (!NeedsFetch(entry, now, forceRefresh))
            {
                return entry.Keys!;
            }

            ThrowWhileBackingOff(entry, now);

            try
            {
                var document = await fetchDocument(cancellationToken);
                entry.Keys = new JsonWebKeySet(document).GetSigningKeys().ToList();
                entry.FetchedAt = now;
                entry.LastSuccessAt = now;
                entry.FailedAt = null;
            }
            catch (Exception) when (entry.Keys != null && IsFresh(entry, now) && !cancellationToken.IsCancellationRequested)
            {
                // Keep serving the last good key set; retry after the forced-refresh interval.
                entry.FetchedAt = now - RefreshInterval + MinimumForcedRefreshInterval;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Nothing usable: no keys yet, or the last good set is too old to trust.
                entry.Keys = null;
                entry.FailedAt = now;
                throw;
            }

            return entry.Keys!;
        }
        finally
        {
            entry.Gate.Release();
        }
    }

    private static bool NeedsFetch(Entry entry, DateTimeOffset now, bool forceRefresh)
    {
        if (entry.Keys == null || !IsFresh(entry, now))
        {
            return true;
        }

        var age = now - entry.FetchedAt;

        return age >= RefreshInterval || (forceRefresh && age >= MinimumForcedRefreshInterval);
    }

    private static bool IsFresh(Entry entry, DateTimeOffset now) => now - entry.LastSuccessAt < MaximumStaleness;

    private static void ThrowWhileBackingOff(Entry entry, DateTimeOffset now)
    {
        if (entry.FailedAt is { } failedAt && now - failedAt < FailureRetryInterval)
        {
            throw new RetryNotDueException();
        }
    }

    /// <summary>The last fetch left no usable keys and the next one is not due yet; nothing was fetched.</summary>
    internal sealed class RetryNotDueException()
        : Exception("The signing keys could not be fetched; the next attempt is not due yet.");

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyList<SecurityKey>? Keys { get; set; }

        public DateTimeOffset FetchedAt { get; set; }

        public DateTimeOffset LastSuccessAt { get; set; }

        public DateTimeOffset? FailedAt { get; set; }
    }
}
