namespace Semantico.Core.Services.Security;

/// <summary>
/// Service for securely hashing and verifying passwords.
/// </summary>
public interface IPasswordHasher
{
    /// <summary>
    /// Hashes a password with a randomly generated salt.
    /// </summary>
    /// <param name="password">The plain text password to hash.</param>
    /// <returns>A tuple containing the hash and salt as Base64 strings.</returns>
    (string hash, string salt) HashPassword(string password);

    /// <summary>
    /// Verifies a password against a stored hash and salt.
    /// </summary>
    /// <param name="password">The plain text password to verify.</param>
    /// <param name="hash">The stored hash as a Base64 string.</param>
    /// <param name="salt">The stored salt as a Base64 string.</param>
    /// <returns>True if the password is correct, false otherwise.</returns>
    bool VerifyPassword(string password, string hash, string salt);
}
