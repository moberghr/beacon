using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Compares a presented secret with the expected one in constant time. Both values are hashed first, so neither the
/// position of the first differing character nor the length of the expected secret shows in the timing.
/// </summary>
internal static class FixedTimeSecretComparer
{
    public static bool Matches(string? presented, string expected)
    {
        if (string.IsNullOrEmpty(presented) || string.IsNullOrEmpty(expected))
        {
            return false;
        }

        var presentedHash = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(expected));

        return CryptographicOperations.FixedTimeEquals(presentedHash, expectedHash);
    }
}
