namespace Beacon.Core.Services.Security;

/// <summary>
/// Service for securely hashing and verifying passwords.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes a password with a randomly generated salt.
    /// </summary>
    /// <param name="password">The plain text password to hash.</param>
    /// <returns>The hash, in the hasher's stored format, and the salt as a Base64 string.</returns>
    (string hash, string salt) HashPassword(string password);

    /// <summary>
    /// Verifies a password against a stored hash and salt.
    /// </summary>
    /// <param name="password">The plain text password to verify.</param>
    /// <param name="hash">The stored hash, as <see cref="HashPassword"/> wrote it (now or in an earlier format).</param>
    /// <param name="salt">The stored salt as a Base64 string.</param>
    /// <returns>True if the password is correct, false otherwise.</returns>
    bool VerifyPassword(string password, string hash, string salt);

    /// <summary>
    /// True when <paramref name="hash"/> was written with weaker parameters than <see cref="HashPassword"/> uses now. After
    /// a successful sign-in Beacon then replaces it with a new hash of the password it just verified. A hasher that does
    /// not override this never asks for a replacement.
    /// </summary>
    bool NeedsRehash(string hash) => false;
}
