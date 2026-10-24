using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Security;

/// <summary>
/// PBKDF2-HMAC-SHA256 with a 256-bit random salt and a 256-bit output, at 600,000 iterations (the OWASP figure for this
/// algorithm). A hash is written as <c>v2$&lt;iterations&gt;$&lt;Base64 hash&gt;</c>, so the work factor can be raised
/// without invalidating stored passwords: a hash with fewer iterations, or in the earlier unversioned format (bare Base64
/// at 100,000 iterations), still verifies, and <see cref="NeedsRehash"/> tells the caller to replace it once the password
/// has been verified. Every verification costs the current iteration count, whatever the stored value: a hash written
/// with fewer iterations is topped up with discarded work, and a missing or malformed one costs a full dummy derivation,
/// so the time a failed sign-in takes does not tell an older or broken credential from a current one.
/// </summary>
internal class PasswordHasher : IPasswordHasher
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

    // The salt of the discarded work; its value does not matter, only that the derivation runs.
    private static readonly byte[] PaddingSalt = RandomNumberGenerator.GetBytes(SaltSize);

    private readonly ILogger<PasswordHasher> _logger;
    private readonly Func<string, byte[], int, byte[]> _deriveKey;

    public PasswordHasher(ILogger<PasswordHasher> logger)
        : this(logger, Pbkdf2)
    {
    }

    // The derivation is replaceable so a test can count the iterations a verification performs.
    internal PasswordHasher(ILogger<PasswordHasher> logger, Func<string, byte[], int, byte[]> deriveKey)
    {
        _logger = logger;
        _deriveKey = deriveKey;
    }

    public (string hash, string salt) HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));
        }

        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);
        var hashBytes = _deriveKey(password, saltBytes, Iterations);

        return (Format(Iterations, hashBytes), Convert.ToBase64String(saltBytes));
    }

    public bool VerifyPassword(string password, string hash, string salt)
    {
        // An empty password is refused for every account alike, before any stored value is looked at.
        if (string.IsNullOrEmpty(password))
        {
            return false;
        }

        if (!TryRead(hash, out var iterations, out var storedHashBytes) || !TryDecode(salt, out var saltBytes))
        {
            if (!string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(salt))
            {
                // Malformed stored material verifies as "wrong password" and permanently locks the user out: logged so
                // support can diagnose it rather than fail silently.
                _logger.LogWarning("Stored credential material is malformed and could not be verified.");
            }

            _deriveKey(password, PaddingSalt, Iterations);
            return false;
        }

        var computedHashBytes = _deriveKey(password, saltBytes, iterations);
        if (iterations < Iterations)
        {
            _deriveKey(password, PaddingSalt, Iterations - iterations);
        }

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

    private static byte[] Pbkdf2(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, Algorithm, HashSize);
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
