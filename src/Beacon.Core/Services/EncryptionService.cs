using System.Security.Cryptography;
using System.Text;

namespace Beacon.Core.Services;

public interface IEncryptionService
{
    string Encrypt(string plainText);
    string Decrypt(string cipherText);
}

internal class EncryptionService : IEncryptionService
{
    // Versioned payload layout (v2): [0x01][12-byte nonce][16-byte GCM tag][ciphertext], base64-encoded.
    // Legacy payload (v1): raw AES-CBC ciphertext with a fixed SHA256-derived IV, base64-encoded.
    private const byte V2Marker = 0x01;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _key;
    private readonly byte[] _legacyIv;

    public EncryptionService(string encryptionKey)
    {
        if (string.IsNullOrWhiteSpace(encryptionKey))
        {
            throw new ArgumentException("Encryption key cannot be null or empty", nameof(encryptionKey));
        }

        // Use SHA256 to create a 32-byte key from the provided key
        using var sha256 = SHA256.Create();
        _key = sha256.ComputeHash(Encoding.UTF8.GetBytes(encryptionKey));

        // Legacy IV (fixed) — kept only so v1 ciphertext written before the GCM upgrade still decrypts.
        _legacyIv = new byte[16];
        Array.Copy(_key, 0, _legacyIv, 0, 16);
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

        // v2: authenticated AES-GCM with a random per-value nonce.
        if (payload.Length > 1 + NonceSize + TagSize && payload[0] == V2Marker)
        {
            return DecryptV2(payload);
        }

        // Legacy v1: AES-CBC with the fixed SHA256-derived IV.
        return DecryptLegacy(payload);
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
        using var srDecrypt = new StreamReader(csDecrypt);

        return srDecrypt.ReadToEnd();
    }
}
