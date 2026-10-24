using Microsoft.Extensions.Caching.Memory;

namespace Beacon.Core.Services;

/// <summary>
/// Beacon's entries in the host's shared <see cref="IMemoryCache"/>: every key starts with <see cref="KeyPrefix"/>, so it
/// never meets a key the host chose, and every entry has a <see cref="MemoryCacheEntryOptions.Size"/> of one, so a host
/// that sets <see cref="MemoryCacheOptions.SizeLimit"/> can still cache Beacon's entries (an entry without a size is
/// refused under a size limit).
/// </summary>
internal static class BeaconCacheEntries
{
    public const string KeyPrefix = "beacon:";

    /// <summary>The cache key of Beacon's entry <paramref name="name"/>.</summary>
    public static string Key(string name)
    {
        return KeyPrefix + name;
    }

    /// <summary>Options for an entry that expires <paramref name="lifetime"/> after it is written.</summary>
    public static MemoryCacheEntryOptions For(TimeSpan lifetime)
    {
        return new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = lifetime,
            Size = 1
        };
    }
}
