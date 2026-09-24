using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Mcp;

/// <summary>
/// Produces <see cref="McpCaller.SubjectHash"/>: lowercase hex HMAC-SHA256 of <c>{tenantId}:{subject}</c>. It is keyed,
/// so someone who holds a tenant's list of <c>oid</c>s cannot reverse audit rows by hashing each one. The HMAC key is
/// derived from <c>Beacon:EncryptionKey</c> for this one purpose (HMAC of a fixed label), so the encryption key itself
/// is never used as the HMAC key. Registered as a singleton by <c>AddBeaconServices</c>; a custom
/// <see cref="IMcpCallerMapper"/> injects it to produce the same hash.
/// </summary>
public sealed class McpCallerSubjectHasher
{
    private const string KeyDerivationLabel = "beacon:mcp-caller-subject-hash:v1";

    private readonly byte[] _key;

    public McpCallerSubjectHasher(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));
        }

        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey), Encoding.UTF8.GetBytes(KeyDerivationLabel));
    }

    public string Hash(string tenantId, string subject)
    {
        var bytes = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes($"{tenantId}:{subject}"));

        return Convert.ToHexStringLower(bytes);
    }
}
