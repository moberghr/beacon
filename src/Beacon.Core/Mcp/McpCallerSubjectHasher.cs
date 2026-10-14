using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace Beacon.Core.Mcp;

/// <summary>
/// Produces <see cref="McpCaller.SubjectHash"/>: lowercase hex HMAC-SHA256 of <c>{tenantId}:{subject}</c>. It is keyed,
/// so someone who holds a tenant's list of <c>oid</c>s cannot reverse audit rows by hashing each one. The HMAC key is
/// derived for this one purpose (HMAC of a fixed label) from <see cref="CallerHashKeyKey"/> when it is set, else from
/// <c>Beacon:EncryptionKey</c>, so neither secret is used as the HMAC key itself. A deployment that will change its
/// encryption key sets <c>Beacon:Mcp:CallerHashKey</c> to the encryption key it has now first: the hashes stay the same
/// (the derivation is the same), and audit rows keep correlating across the change. Registered as a singleton by
/// <c>AddBeaconServices</c>; a custom <see cref="IMcpCallerMapper"/> injects it to produce the same hash.
/// </summary>
public sealed class McpCallerSubjectHasher
{
    /// <summary>The configuration key of the optional secret the subject hashes are derived from.</summary>
    public const string CallerHashKeyKey = "Beacon:Mcp:CallerHashKey";

    private const string KeyDerivationLabel = "beacon:mcp-caller-subject-hash:v1";

    private readonly byte[] _key;

    /// <param name="encryptionKey">The secret the HMAC key is derived from: the caller hash key, or the encryption key.</param>
    public McpCallerSubjectHasher(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));
        }

        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey), Encoding.UTF8.GetBytes(KeyDerivationLabel));
    }

    /// <summary>
    /// The secret the subject hashes are derived from: <see cref="CallerHashKeyKey"/> when set, else
    /// <paramref name="encryptionKey"/>. A placeholder (<c>${...}</c>, which configuration does not expand) fails startup;
    /// the message never contains the value.
    /// </summary>
    public static string KeySource(IConfiguration configuration, string encryptionKey)
    {
        var callerHashKey = configuration[CallerHashKeyKey];
        if (string.IsNullOrWhiteSpace(callerHashKey))
        {
            return encryptionKey;
        }

        if (callerHashKey.Contains("${", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{CallerHashKeyKey} contains '${{': .NET configuration does not expand placeholders, so the key would be the placeholder text itself.");
        }

        return callerHashKey;
    }

    public string Hash(string tenantId, string subject)
    {
        var bytes = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{tenantId}:{subject}"));

        return Convert.ToHexStringLower(bytes);
    }
}
