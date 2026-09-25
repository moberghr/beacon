using System.Collections.Concurrent;
using Microsoft.IdentityModel.Tokens;

namespace Beacon.Core.Authentication;

/// <summary>
/// Caches the signing keys of a JWKS endpoint so bearer validation does not fetch the key set on every request.
/// Keys are refreshed after <see cref="RefreshInterval"/>; a token signed with an unknown key id may force an early
/// refresh (key rotation), but at most once per <see cref="MinimumForcedRefreshInterval"/> so a flood of forged tokens
/// cannot turn Beacon into a JWKS hammer. When a refresh fails and keys are already cached, the cached keys are kept.
/// </summary>
internal sealed class JwksSigningKeyCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    public static readonly TimeSpan MinimumForcedRefreshInterval = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    /// <summary>The process-wide cache the provider uses when none is supplied.</summary>
    public static JwksSigningKeyCache Shared { get; } = new(TimeProvider.System);

    /// <summary>
    /// The endpoint's signing keys, fetched through <paramref name="fetchDocument"/> when nothing is cached, the cache
    /// is older than <see cref="RefreshInterval"/>, or <paramref name="forceRefresh"/> is set and the last fetch is
    /// older than <see cref="MinimumForcedRefreshInterval"/>.
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

        await entry.Gate.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed while this one waited.
            now = timeProvider.GetUtcNow();
            if (!NeedsFetch(entry, now, forceRefresh))
            {
                return entry.Keys!;
            }

            try
            {
                var document = await fetchDocument(cancellationToken);
                entry.Keys = new JsonWebKeySet(document).GetSigningKeys().ToList();
                entry.FetchedAt = now;
            }
            catch (Exception) when (entry.Keys != null)
            {
                // Keep serving the last good key set; retry after the forced-refresh interval.
                entry.FetchedAt = now - RefreshInterval + MinimumForcedRefreshInterval;
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
        if (entry.Keys == null)
        {
            return true;
        }

        var age = now - entry.FetchedAt;

        return age >= RefreshInterval || (forceRefresh && age >= MinimumForcedRefreshInterval);
    }

    private sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public IReadOnlyList<SecurityKey>? Keys { get; set; }

        public DateTimeOffset FetchedAt { get; set; }
    }
}
