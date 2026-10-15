using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Authentication;

/// <summary>
/// A short SHA-256 prefix of a token subject, for logs: repeated refusals of the same subject correlate, but the
/// subject itself is never written.
/// </summary>
internal static class SubjectFingerprint
{
    private const int Length = 12;

    public static string Of(string? subject)
    {
        if (string.IsNullOrEmpty(subject))
        {
            return "-";
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))[..Length];
    }
}
