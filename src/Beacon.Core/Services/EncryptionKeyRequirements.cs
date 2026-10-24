using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Services;

/// <summary>
/// What <c>Beacon:EncryptionKey</c> must be, checked at startup in every environment: base64 that decodes to at least
/// <see cref="MinimumKeyBytes"/> random bytes (<c>openssl rand -base64 32</c>), not a value the sample host or the
/// documentation ever printed (anyone can read those), and never a <c>${...}</c> placeholder (.NET configuration does not
/// expand it, so the key would be the placeholder text itself). Bytes that are not random are refused too: all printable
/// text (a passphrase or a word written as base64) or fewer than <see cref="MinimumDistinctByteValues"/> distinct values
/// (a repeated pattern). The messages never contain the value.
/// </summary>
internal static class EncryptionKeyRequirements
{
    public const int MinimumKeyBytes = 32;

    /// <summary>The fewest distinct byte values a key may have; 32 random bytes have about 30.</summary>
    public const int MinimumDistinctByteValues = 16;

    /// <summary><c>Beacon:EncryptionKey</c>, whose replaced values stay readable through <c>Beacon:PreviousEncryptionKeys</c>.</summary>
    internal static KeySetting EncryptionKey { get; } = new("Beacon:EncryptionKey", "Beacon__EncryptionKey", KeepsPreviousKeys: true);

    /// <summary><c>Beacon:Embed:SigningKey</c>: no previous keys, a replaced key only ends the tokens it signed.</summary>
    internal static KeySetting EmbedSigningKey { get; } = new("Beacon:Embed:SigningKey", "Beacon__Embed__SigningKey", KeepsPreviousKeys: false);

    // SHA-256 (lowercase hex) of the key values the sample host, the README and earlier versions of the documentation
    // printed. They are public, so whatever they encrypt can be read by anyone; kept as digests so the values themselves
    // stay out of the source.
    private static readonly HashSet<string> PublishedKeys = new(StringComparer.Ordinal)
    {
        "1d72a6a9a78507f566ae64d926d3f0e8159cd4c40a7b5bcde9e6aa3947ae7d5c",
        "c851556dc6f367c864451fe66890c800227cfe46e984fd4c7ca39efbfc946286",
        "267527a2e17e403cd1639b5592564bb5de2a9100e65b8a90a57634cce046a484",
        "a52bb66f5ad548cf1c2afd6cd3403e953fb290a5085757424fd42e03b78ef6e4",
        "8d976fab83f55c8581b4849b10e68a70ae9965adf5bd07668d9e4233b1051fa6",
        "4bb0bdc3129b40e470e0d2a1439a9d334c90530152ad4a7805a9f678185c736e",
        "5c7a24b75b4dac67ad88469a1389bb53a65d8f82cc7ca2923f5f29b4fd5246f2",
        "0a53c0992edae8ceb04c7a32ee47b49ad0b229c17b073e8c24683111c843d45e",
        "5e686bc9e008e35c68fd5c55e184aed4beb956041cd9c729b3ddd37820890fdd",
        "e2d8a1d355e6188dce41af0ce7560c885c86f148a697e3ad32922081b4b07793",
        "0d854a7c83e01c405f7aa9d3115c98ba4e7d2a0cc6a99b6264641643011df309",
        "9210a4f5a4d2085f71a55fbb1c07bcc31aa0ee3766d82d10292f2938ac86517b"
    };

    /// <summary>The digests of the published key values: lowercase hex SHA-256 of the UTF-8 value.</summary>
    internal static IReadOnlySet<string> PublishedKeyDigests => PublishedKeys;

    /// <summary>Why <paramref name="key"/> cannot be the encryption key, or null when it can.</summary>
    public static string? Problem(string? key)
    {
        return Problem(key, PublishedKeys, EncryptionKey);
    }

    /// <summary>Why <paramref name="key"/> cannot be the key <paramref name="setting"/> names, or null when it can.</summary>
    internal static string? Problem(string? key, KeySetting setting)
    {
        return Problem(key, PublishedKeys, setting);
    }

    internal static string? Problem(string? key, IReadOnlySet<string> publishedKeyDigests)
    {
        return Problem(key, publishedKeyDigests, EncryptionKey);
    }

    internal static string? Problem(string? key, IReadOnlySet<string> publishedKeyDigests, KeySetting setting)
    {
        var name = setting.Name;
        var howToGenerate =
            $"Generate one with `openssl rand -base64 32` and supply it through a secret store or the {setting.EnvironmentVariable} environment variable.";
        var howToKeepReading = setting.KeepsPreviousKeys
            ? $" To keep reading the values encrypted with the key configured now, move that key to Beacon:PreviousEncryptionKeys (decrypt only) and set a new {name}."
            : string.Empty;

        if (string.IsNullOrWhiteSpace(key))
        {
            return $"{name} must be configured. " + howToGenerate;
        }

        if (key.Contains("${", StringComparison.Ordinal))
        {
            return $"{name} contains '${{': .NET configuration does not expand placeholders, so the key would be "
                + "the placeholder text itself. " + howToGenerate;
        }

        var value = key.Trim();
        if (publishedKeyDigests.Contains(Digest(value)))
        {
            return $"{name} is a publicly known value (the sample placeholder or a key printed in the "
                + "documentation), so anything it protects can be read or forged by anyone. " + howToGenerate + howToKeepReading;
        }

        var bytes = new byte[value.Length];
        if (!Convert.TryFromBase64String(value, bytes, out var length) || length < MinimumKeyBytes)
        {
            return $"{name} must be base64 that decodes to at least {MinimumKeyBytes} random bytes. "
                + howToGenerate + howToKeepReading;
        }

        var decoded = bytes.AsSpan(0, length);
        if (IsPrintableText(decoded) || CountDistinct(decoded) < MinimumDistinctByteValues)
        {
            return $"{name} decodes to bytes that are not random (printable text, or a repeated pattern): "
                + $"it must be base64 of at least {MinimumKeyBytes} random bytes, not a passphrase encoded as base64. "
                + howToGenerate + howToKeepReading;
        }

        return null;
    }

    /// <summary>
    /// The decoded bytes of a key <see cref="Problem(string?, KeySetting)"/> accepted; throws for one it would refuse.
    /// </summary>
    internal static byte[] Decode(string key)
    {
        return Convert.FromBase64String(key.Trim());
    }

    internal static string Digest(string value)
    {
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    }

    // Every byte is printable ASCII (space to tilde): text, such as a passphrase, written as base64.
    private static bool IsPrintableText(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return true;
    }

    private static int CountDistinct(ReadOnlySpan<byte> bytes)
    {
        var seen = new HashSet<byte>();
        foreach (var value in bytes)
        {
            seen.Add(value);
        }

        return seen.Count;
    }
}

/// <summary>A key setting the requirements name in their messages, and whether it has a decrypt-only list of previous keys.</summary>
internal sealed record KeySetting(string Name, string EnvironmentVariable, bool KeepsPreviousKeys);
