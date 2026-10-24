using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Beacon.Core.Services.Embed;

/// <summary>
/// The secret a host's backend exchanges for embed tokens: <see cref="Prefix"/> followed by 32 random bytes as base64url,
/// shown once when the grant is created. Only its SHA-256 (lowercase hex, §1.3) is stored, and an exchange finds the grant
/// by that hash. Rotating it means revoking the grant and creating another.
/// </summary>
internal static class EmbedGrantSecret
{
    public const string Prefix = "bge_";

    private const int SecretBytes = 32;

    // 32 bytes as unpadded base64url are 43 characters.
    private const int EncodedLength = 43;

    private static readonly System.Buffers.SearchValues<char> Base64UrlCharacters =
        System.Buffers.SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_");

    /// <summary>A new secret and its hash.</summary>
    public static (string Secret, string Hash) Create()
    {
        var secret = Prefix + Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(SecretBytes));

        return (secret, Hash(secret));
    }

    /// <summary>The stored form of <paramref name="secret"/>: its SHA-256 as lowercase hex.</summary>
    public static string Hash(string secret)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    }

    /// <summary>Whether <paramref name="value"/> has a secret's form (checked before any lookup).</summary>
    public static bool IsWellFormed(string? value)
    {
        return value != null
            && value.Length == Prefix.Length + EncodedLength
            && value.StartsWith(Prefix, StringComparison.Ordinal)
            && value.AsSpan(Prefix.Length).IndexOfAnyExcept(Base64UrlCharacters) < 0;
    }

    /// <summary>A new public id for a grant: 16 random bytes as 32 lowercase hex characters.</summary>
    public static string NewPublicId()
    {
        return Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    }
}
