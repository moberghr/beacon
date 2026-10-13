using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// The approved-definition hash a governed resource (a migration job, a Custom SQL rule, a recipient) carries while the
/// change approval workflow governs it: null when the workflow does not govern it (it works as before),
/// <see cref="NothingApproved"/> when it was created under the workflow and nothing is approved yet, else the hash of the
/// definition a second person approved. The resource works only while its definition hashes to its approved hash.
/// </summary>
public static class ChangeApprovalHashes
{
    /// <summary>A governed resource none of whose definitions is approved yet: it matches no definition.</summary>
    public const string NothingApproved = "none";

    /// <summary>
    /// True when a resource with <paramref name="approvedHash"/> may work holding a definition that hashes to
    /// <paramref name="currentHash"/>: it is not governed, or that definition is the approved one. A definition that cannot
    /// be read (null) is never approved.
    /// </summary>
    internal static bool Allows(string? approvedHash, string? currentHash)
    {
        return approvedHash == null
            || (currentHash != null && string.Equals(approvedHash, currentHash, StringComparison.Ordinal));
    }

    /// <summary>Lowercase hex SHA-256 of <paramref name="text"/> (UTF-8), character for character.</summary>
    internal static string Sha256(string text)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
