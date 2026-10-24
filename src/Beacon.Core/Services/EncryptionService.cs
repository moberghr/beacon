using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Services;

public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}

/// <summary>
/// AES-256 encryption of values at rest (data-source connection strings, recipient secrets, sensitive settings) with
/// <c>Beacon:EncryptionKey</c>. New values are written in the v2 format below with the configured key. A value is read with
/// the configured key and, failing that, with each of <c>Beacon:PreviousEncryptionKeys</c> (decrypt only), so a key can be
/// replaced without rewriting what is stored: a value moves to the new key when it is next saved, or when
/// <see cref="IStoredValueReEncryptionService"/> re-encrypts it. The first value each previous key decrypts is logged
/// (Information, the key's position in the list only).
/// <para>
/// A value in the legacy v1 format (AES-CBC with a fixed IV and no authentication, written before the v2 format) is still
/// read while <c>Beacon:Encryption:AllowLegacyCbc</c> is on (the default); the first one read is logged (Warning, once per
/// process), and <see cref="IStoredValueReEncryptionService"/> rewrites such values in the v2 format. Once it reports none
/// left, turn the setting off and v1 values are refused.
/// </para>
/// </summary>
/// <remarks>
/// The AES key is SHA-256 of the configured key text. It is kept, not replaced by HKDF: startup accepts only a key of at
/// least 32 random bytes (<see cref="EncryptionKeyRequirements"/>), for which one SHA-256 is a sound extraction step (a
/// salted or stretched derivation only adds strength to low-entropy passphrases, which are refused); and a new derivation
/// would need a new payload version that an earlier Beacon version cannot read, so a rollback would lose every value
/// written in between.
/// </remarks>
internal class EncryptionService : IEncryptionService
{
    // Versioned payload layout (v2): [0x01][12-byte nonce][16-byte GCM tag][ciphertext], base64-encoded.
    // Legacy payload (v1): raw AES-CBC ciphertext with a fixed SHA256-derived IV, base64-encoded.
    private const byte V2Marker = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int LegacyBlockSize = 16;

    // A wrong key can leave legacy CBC padding intact by chance; the garbage it yields is then not UTF-8 and is refused.
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _key;
    private readonly byte[] _legacyIv;
    private readonly IReadOnlyList<PreviousKey> _previousKeys;
    private readonly ILogger? _logger;
    private readonly bool _allowLegacyFormat;
    private int _legacyFormatLogged;

    public EncryptionService(string encryptionKey)
        : this(encryptionKey, [])
    {
    }

    /// <param name="encryptionKey">The key new values are encrypted with.</param>
    /// <param name="previousKeys">Keys values written earlier may be encrypted with: read only, never used to encrypt.</param>
    /// <param name="logger">Where the first use of each previous key, and the first legacy v1 value read, are logged.</param>
    /// <param name="allowLegacyFormat">Whether a value in the legacy v1 format is still read (refused when false).</param>
    public EncryptionService(string encryptionKey, IEnumerable<string> previousKeys, ILogger? logger = null, bool allowLegacyFormat = true)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));
        }

        // Use SHA256 to create a 32-byte key from the provided key
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(encryptionKey));

        // Legacy IV (fixed) — kept only so v1 ciphertext written before the GCM upgrade still decrypts.
        _legacyIv = new byte[16];
        Array.Copy(_key, 0, _legacyIv, 0, 16);

        // Each previous key keeps its position in Beacon:PreviousEncryptionKeys, which is how the log names it.
        _previousKeys = previousKeys
            .Select((x, i) => (Key: x, Index: i))
            .Where(x => !string.IsNullOrWhiteSpace(x.Key))
            .Where(x => !string.Equals(x.Key, encryptionKey, StringComparison.Ordinal))
            .DistinctBy(x => x.Key, StringComparer.Ordinal)
            .Select(x => new PreviousKey(new EncryptionService(x.Key, [], null, allowLegacyFormat), x.Index))
            .ToList();
        _logger = logger;
        _allowLegacyFormat = allowLegacyFormat;
    }

    /// <summary>Which configured key decrypts a stored value.</summary>
    internal enum KeyMatch
    {
        /// <summary>The current key (or the value is empty): nothing to re-encrypt.</summary>
        Current,

        /// <summary>One of the previous keys only: re-encrypt it with the current key.</summary>
        Previous,

        /// <summary>No configured key: it cannot be read.</summary>
        None
    }

    public string Encrypt(string plainText)
    {
        if (string.IsNullOrEmpty(plainText))
        {
            return plainText;
        }

        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[TagSize];

        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        // Layout: [marker][nonce][tag][ciphertext]
        var payload = new byte[1 + NonceSize + TagSize + cipherBytes.Length];
        payload[0] = V2Marker;
        Buffer.BlockCopy(nonce, 0, payload, 1, NonceSize);
        Buffer.BlockCopy(tag, 0, payload, 1 + NonceSize, TagSize);
        Buffer.BlockCopy(cipherBytes, 0, payload, 1 + NonceSize + TagSize, cipherBytes.Length);

        return Convert.ToBase64String(payload);
    }

    public string Decrypt(string cipherText)
    {
        if (string.IsNullOrEmpty(cipherText))
        {
            return cipherText;
        }

        var payload = Convert.FromBase64String(cipherText);

        try
        {
            var plainText = DecryptPayload(payload, out var legacyFormat);
            LogFirstLegacyFormat(legacyFormat);

            return plainText;
        }
        catch (CryptographicException) when (_previousKeys.Count > 0)
        {
            foreach (var previous in _previousKeys)
            {
                try
                {
                    var plainText = previous.Service.DecryptPayload(payload, out var legacyFormat);
                    LogFirstUse(previous);
                    LogFirstLegacyFormat(legacyFormat);

                    return plainText;
                }
                catch (CryptographicException)
                {
                    // Not encrypted with this previous key either; the next one is tried.
                }
            }

            // No key decrypts it: the configured key's failure is reported.
            throw;
        }
    }

    /// <summary>Which configured key decrypts <paramref name="cipherText"/>; a value that is not base64 matches none.</summary>
    internal KeyMatch Identify(string? cipherText)
    {
        return Identify(cipherText, out _);
    }

    /// <summary>
    /// Which configured key decrypts <paramref name="cipherText"/>, and whether it is in the legacy v1 format (to be
    /// rewritten in the v2 format whichever key decrypts it).
    /// </summary>
    internal KeyMatch Identify(string? cipherText, out bool legacyFormat)
    {
        legacyFormat = false;
        if (string.IsNullOrEmpty(cipherText))
        {
            return KeyMatch.Current;
        }

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(cipherText);
        }
        catch (FormatException)
        {
            return KeyMatch.None;
        }

        if (CanDecrypt(payload, out legacyFormat))
        {
            return KeyMatch.Current;
        }

        foreach (var previous in _previousKeys)
        {
            if (previous.Service.CanDecrypt(payload, out legacyFormat))
            {
                return KeyMatch.Previous;
            }
        }

        return KeyMatch.None;
    }

    private bool CanDecrypt(byte[] payload, out bool legacyFormat)
    {
        try
        {
            DecryptPayload(payload, out legacyFormat);

            return true;
        }
        catch (CryptographicException)
        {
            legacyFormat = false;

            return false;
        }
    }

    private string DecryptPayload(byte[] payload, out bool legacyFormat)
    {
        // v2: authenticated AES-GCM with a random per-value nonce. A legacy v1 value whose first ciphertext byte happens
        // to be the v2 marker fails the GCM tag check; a whole number of CBC blocks is then read as legacy v1.
        legacyFormat = false;
        if (payload.Length > 1 + NonceSize + TagSize && payload[0] == V2Marker)
        {
            try
            {
                return DecryptV2(payload);
            }
            catch (CryptographicException) when (_allowLegacyFormat && payload.Length % LegacyBlockSize == 0)
            {
                var plainText = DecryptLegacy(payload);
                legacyFormat = true;

                return plainText;
            }
        }

        if (!_allowLegacyFormat)
        {
            throw new CryptographicException("The value is not in the current format, and the legacy format is not read.");
        }

        // Legacy v1: AES-CBC with the fixed SHA256-derived IV.
        var legacyPlainText = DecryptLegacy(payload);
        legacyFormat = true;

        return legacyPlainText;
    }

    private void LogFirstUse(PreviousKey previous)
    {
        if (_logger != null && Interlocked.Exchange(ref previous.Logged, 1) == 0)
        {
            _logger.LogInformation(
                "A stored value was decrypted with Beacon:PreviousEncryptionKeys entry {Index}, not the current key; it moves to the current key when it is next saved or re-encrypted. Logged once per key",
                previous.Index);
        }
    }

    private void LogFirstLegacyFormat(bool legacyFormat)
    {
        if (legacyFormat && _logger != null && Interlocked.Exchange(ref _legacyFormatLogged, 1) == 0)
        {
            _logger.LogWarning(
                "A stored value was read in the legacy unauthenticated format; it moves to the current format when it is next saved or re-encrypted (IStoredValueReEncryptionService), after which {Setting} can be turned off. Logged once",
                ServiceConfiguration.AllowLegacyCbcKey);
        }
    }

    private string DecryptV2(byte[] payload)
    {
        var nonce = new byte[NonceSize];
        var tag = new byte[TagSize];
        var cipherBytes = new byte[payload.Length - 1 - NonceSize - TagSize];

        Buffer.BlockCopy(payload, 1, nonce, 0, NonceSize);
        Buffer.BlockCopy(payload, 1 + NonceSize, tag, 0, TagSize);
        Buffer.BlockCopy(payload, 1 + NonceSize + TagSize, cipherBytes, 0, cipherBytes.Length);

        var plainBytes = new byte[cipherBytes.Length];

        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    private string DecryptLegacy(byte[] payload)
    {
        using var aes = Aes.Create();
        aes.Key = _key;
        aes.IV = _legacyIv;

        using var decryptor = aes.CreateDecryptor();
        using var msDecrypt = new MemoryStream(payload);
        using var csDecrypt = new CryptoStream(msDecrypt, decryptor, CryptoStreamMode.Read);
        using var srDecrypt = new StreamReader(csDecrypt, StrictUtf8);

        try
        {
            return srDecrypt.ReadToEnd();
        }
        catch (DecoderFallbackException ex)
        {
            throw new CryptographicException("The value could not be decrypted with this key.", ex);
        }
    }

    // A previous key, its position in Beacon:PreviousEncryptionKeys, and whether its first use was logged.
    private sealed class PreviousKey(EncryptionService service, int index)
    {
        public int Logged;

        public EncryptionService Service { get; } = service;

        public int Index { get; } = index;
    }
}
