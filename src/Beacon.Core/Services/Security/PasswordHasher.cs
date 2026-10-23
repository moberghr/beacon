using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a 256-bit random salt and a 256-bit output, at 600,000 iterations (the OWASP figure for this
/// algorithm). A hash is written as <c>v2$&lt;iterations&gt;$&lt;Base64 hash&gt;</c>, so the work factor can be raised
/// without invalidating stored passwords: a hash with fewer iterations, or in the earlier unversioned format (bare Base64
/// at 100,000 iterations), still verifies, and <see cref="NeedsRehash"/> tells the caller to replace it once the password
/// has been verified.
/// </summary>
internal class PasswordHasher(ILogger<PasswordHasher> logger) : IPasswordHasher
{
    internal const int Iterations = 600_000;

    // The unversioned format's fixed count, and the lowest a versioned hash may carry.
    private const int UnversionedIterations = 100_000;

    // Bounds the work a stored value can demand of one verification.
    private const int MaxIterations = 10_000_000;

    private const int SaltSize = 32; // 256 bits
    private const int HashSize = 32; // 256 bits
    private const string VersionPrefix = "v2$";
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public (string hash, string salt) HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));
        }

        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, Iterations, Algorithm, HashSize);

        return (Format(Iterations, hashBytes), Convert.ToBase64String(saltBytes));
    }

    public bool VerifyPassword(string password, string hash, string salt)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
        {
            return false;
        }

        if (!TryRead(hash, out var iterations, out var storedHashBytes) || !TryDecode(salt, out var saltBytes))
        {
            // Malformed stored material verifies as "wrong password" and permanently locks the user out: logged so support
            // can diagnose it rather than fail silently.
            logger.LogWarning("Stored credential material is malformed and could not be verified.");
            return false;
        }

        var computedHashBytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations, Algorithm, HashSize);

        // Constant-time, so the comparison's timing tells nothing about how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(computedHashBytes, storedHashBytes);
    }

    public bool NeedsRehash(string hash)
    {
        return TryRead(hash, out var iterations, out _) && iterations < Iterations;
    }

    /// <summary>A well-formed hash at the current iteration count that no password matches.</summary>
    internal static string UnmatchableHash()
    {
        return Format(Iterations, RandomNumberGenerator.GetBytes(HashSize));
    }

    private static string Format(int iterations, byte[] hashBytes)
    {
        return VersionPrefix + iterations.ToString(CultureInfo.InvariantCulture) + "$" + Convert.ToBase64String(hashBytes);
    }

    private static bool TryRead(string? hash, out int iterations, out byte[] hashBytes)
    {
        iterations = 0;
        hashBytes = [];
        if (string.IsNullOrEmpty(hash))
        {
            return false;
        }

        if (!hash.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            // Unversioned: written before the format carried its parameters. Anything carrying a '$' is not Base64, so a
            // value that looks like another version fails here.
            iterations = UnversionedIterations;
            return TryDecode(hash, out hashBytes) && hashBytes.Length == HashSize;
        }

        var parts = hash.Split('$');
        if (parts.Length != 3
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out iterations)
            || iterations is < UnversionedIterations or > MaxIterations)
        {
            iterations = 0;
            return false;
        }

        return TryDecode(parts[2], out hashBytes) && hashBytes.Length == HashSize;
    }

    private static bool TryDecode(string value, out byte[] bytes)
    {
        var buffer = new byte[(value.Length * 3 / 4) + 3];
        if (Convert.TryFromBase64String(value, buffer, out var written) && written > 0)
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }
}
