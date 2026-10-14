using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Mcp;

/// <summary>
/// The keyed hash MCP audit rows carry of what a call received (<c>McpAuditLog.ContentHash</c>) and of each statement it
/// ran (<c>McpAuditStatement.SqlHash</c>): lowercase hex HMAC-SHA256, written whether or not the content itself is kept, so
/// calls and statements with the same content can be matched (and a disclosed text checked against the trail) without
/// keeping it, while nobody without the key can test guesses against the stored hashes. The HMAC key is derived for this
/// one purpose (HMAC of a fixed label) from the secret the MCP caller hashes are derived from
/// (<c>Beacon:Mcp:CallerHashKey</c>, else <c>Beacon:EncryptionKey</c>; see <see cref="McpCallerSubjectHasher.KeySource"/>),
/// so it is never that secret itself and stays stable across an encryption-key change that keeps the caller hash key.
/// Registered as a singleton by <c>AddBeaconServices</c>.
/// </summary>
public sealed class McpContentHasher
{
    private const string KeyDerivationLabel = "beacon:mcp-audit-content-hash:v1";

    private readonly byte[] _key;

    /// <param name="keySource">The secret the HMAC key is derived from.</param>
    public McpContentHasher(string keySource)
    {
        if (string.IsNullOrWhiteSpace(keySource))
        {
            throw new ArgumentException("The content hash key source cannot be empty.", nameof(keySource));
        }

        _key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(keySource), Encoding.UTF8.GetBytes(KeyDerivationLabel));
    }

    // What an absent content is hashed as: a byte UTF-8 never produces, so no text, the empty text included, hashes the same.
    private static readonly byte[] AbsentContent = [0xFF];

    /// <summary>The keyed hash of <paramref name="content"/>; an absent content has a hash of its own, apart from the empty text's.</summary>
    public string Hash(string? content)
    {
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, content == null ? AbsentContent : Encoding.UTF8.GetBytes(content)));
    }
}
