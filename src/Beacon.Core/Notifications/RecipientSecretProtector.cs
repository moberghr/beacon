using Beacon.Core.Services;

namespace Beacon.Core.Notifications;

/// <summary>
/// Encrypts recipient destinations and custom headers at rest with the same <see cref="IEncryptionService"/>
/// (<c>Beacon:EncryptionKey</c>) that protects data-source connection strings. A stored value carries
/// <see cref="EncryptedPrefix"/>; a value without it was written before encryption and is read as plaintext, so existing
/// rows keep working until they are next saved or <see cref="IRecipientSecretEncryptionService"/> encrypts them.
/// </summary>
internal sealed class RecipientSecretProtector(IEncryptionService encryptionService)
{
    /// <summary>
    /// Marks an encrypted value. No valid plaintext value starts with it: URLs start with their scheme, and Jira site
    /// names, email addresses and header JSON cannot begin with <c>enc:</c>.
    /// </summary>
    public const string EncryptedPrefix = "enc:";

    public static bool IsProtected(string? stored)
    {
        return stored != null && stored.StartsWith(EncryptedPrefix, StringComparison.Ordinal);
    }

    public string Protect(string value)
    {
        return EncryptedPrefix + encryptionService.Encrypt(value);
    }

    /// <summary>Protects a value that may be absent; null or empty stays null.</summary>
    public string? ProtectOptional(string? value)
    {
        return string.IsNullOrEmpty(value) ? null : Protect(value);
    }

    /// <summary>The plaintext of a stored value: decrypted when protected, as stored when written before encryption.</summary>
    public string Unprotect(string stored)
    {
        return IsProtected(stored)
            ? encryptionService.Decrypt(stored[EncryptedPrefix.Length..])
            : stored;
    }

    public string? UnprotectOptional(string? stored)
    {
        return string.IsNullOrEmpty(stored) ? null : Unprotect(stored);
    }

    /// <summary>Like <see cref="UnprotectOptional"/>, but null instead of throwing when the value cannot be decrypted.</summary>
    public string? TryUnprotect(string? stored)
    {
        try
        {
            return UnprotectOptional(stored);
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
    }
}
