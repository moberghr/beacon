using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// Keyed hashes of where a recipient delivers (<see cref="RecipientDestinationDefinition"/>), which covers its secrets: an
/// HMAC-SHA256 under a key derived for this one purpose (HKDF-SHA256 with its own label) from <c>Beacon:EncryptionKey</c>,
/// so someone who reads the stored hash cannot test guesses of a destination or header value against it. A stored hash
/// carries <see cref="Prefix"/>. A hash written under a key now listed in <c>Beacon:PreviousEncryptionKeys</c> still
/// matches: verification tries the current key, then each previous one, so replacing the key holds no governed
/// recipient, and <see cref="Services.IStoredValueReEncryptionService"/> rewrites such hashes with the current key.
/// Fingerprints are short keyed digests under another derived key, shown so two people can compare a destination or a
/// header value out of band without either seeing it; they reveal nothing without the key.
/// </summary>
internal sealed class RecipientDestinationHasher
{
    /// <summary>Marks a keyed hash (an earlier, unkeyed form is never accepted).</summary>
    public const string Prefix = "v2:";

    /// <summary>How many hex characters a fingerprint shows.</summary>
    public const int FingerprintLength = 12;

    private const string HashLabel = "beacon:recipient-destination-hash:v2";
    private const string FingerprintLabel = "beacon:recipient-destination-fingerprint:v1";

    private readonly byte[] _current;
    private readonly IReadOnlyList<byte[]> _previous;
    private readonly byte[] _fingerprint;

    /// <param name="encryptionKey">The key new hashes are written under: <c>Beacon:EncryptionKey</c>.</param>
    /// <param name="previousKeys">The keys hashes written earlier may be under: <c>Beacon:PreviousEncryptionKeys</c> (verified, never written).</param>
    public RecipientDestinationHasher(string encryptionKey, IEnumerable<string>? previousKeys = null)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));
        }

        _current = Derive(encryptionKey, HashLabel);
        _fingerprint = Derive(encryptionKey, FingerprintLabel);
        _previous = (previousKeys ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Where(x => !string.Equals(x, encryptionKey, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Select(x => Derive(x, HashLabel))
            .ToList();
    }

    /// <summary>True when a previous key is configured, so a stored hash may have been written under one.</summary>
    public bool HasPreviousKeys => _previous.Count > 0;

    /// <summary>The keyed hash of <paramref name="canonical"/> under the current key, as it is stored.</summary>
    public string Hash(string canonical)
    {
        return Under(_current, canonical);
    }

    /// <summary>
    /// True when <paramref name="stored"/> is the keyed hash of <paramref name="canonical"/> under the current key or a
    /// previous one. A missing, unkeyed or malformed value matches nothing.
    /// </summary>
    public bool Matches(string? stored, string canonical)
    {
        if (stored == null || !stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return Same(stored, Under(_current, canonical)) || _previous.Any(x => Same(stored, Under(x, canonical)));
    }

    /// <summary>True when <paramref name="stored"/> is the keyed hash of <paramref name="canonical"/> under the current key.</summary>
    public bool IsCurrent(string? stored, string canonical)
    {
        return stored != null && Same(stored, Under(_current, canonical));
    }

    /// <summary>A short keyed fingerprint of <paramref name="value"/> (lowercase hex), for comparing it out of band.</summary>
    public string Fingerprint(string value)
    {
        return Convert.ToHexStringLower(HMACSHA256.HashData(_fingerprint, Encoding.UTF8.GetBytes(value)))[..FingerprintLength];
    }

    private static byte[] Derive(string key, string label)
    {
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(key), 32, [], Encoding.UTF8.GetBytes(label));
    }

    // Base64url keeps the stored value within the column's 64 characters.
    private static string Under(byte[] key, string canonical)
    {
        return Prefix + Base64Url.EncodeToString(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool Same(string first, string second)
    {
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(first), Encoding.UTF8.GetBytes(second));
    }
}
