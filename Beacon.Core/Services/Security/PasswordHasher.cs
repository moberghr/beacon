using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services.Security;

/// <summary>
/// Password hasher using PBKDF2 with SHA256.
/// Uses 100,000 iterations and 256-bit output as recommended by OWASP.
/// </summary>
internal class PasswordHasher(ILogger<PasswordHasher> logger) : IPasswordHasher
{
    private const int SaltSize = 32; // 256 bits
    private const int HashSize = 32; // 256 bits
    private const int Iterations = 100_000;
    private static readonly HashAlgorithmName Algorithm = HashAlgorithmName.SHA256;

    public (string hash, string salt) HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password cannot be null or empty.", nameof(password));

        // Generate a random salt
        var saltBytes = RandomNumberGenerator.GetBytes(SaltSize);

        // Hash the password with PBKDF2
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            saltBytes,
            Iterations,
            Algorithm,
            HashSize);

        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public bool VerifyPassword(string password, string hash, string salt)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
            return false;

        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var storedHashBytes = Convert.FromBase64String(hash);

            // Hash the provided password with the stored salt
            var computedHashBytes = Rfc2898DeriveBytes.Pbkdf2(
                password,
                saltBytes,
                Iterations,
                Algorithm,
                HashSize);

            // Use constant-time comparison to prevent timing attacks
            return CryptographicOperations.FixedTimeEquals(computedHashBytes, storedHashBytes);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            // Malformed stored hash/salt (bad Base64, wrong length) verifies as "wrong password" and
            // permanently locks the user out — log so support can diagnose rather than fail silently.
            logger.LogWarning(ex, "Stored credential material is malformed and could not be verified.");
            return false;
        }
    }
}
