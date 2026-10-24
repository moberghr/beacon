using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class EncryptionServiceTests
{
    private const string Key = "unit-test-encryption-key-1234567890";

    private static EncryptionService Create(string key = Key) => new(key);

    [Test]
    public void EncryptDecrypt_RoundTrips()
    {
        var service = Create();
        const string plain = "Server=db;User=sa;Password=p@ss w0rd;";

        var cipher = service.Encrypt(plain);

        cipher.Should().NotBe(plain);
        service.Decrypt(cipher).Should().Be(plain);
    }

    [TestCase("")]
    [TestCase(null)]
    public void Encrypt_EmptyOrNull_PassesThrough(string? value)
    {
        Create().Encrypt(value!).Should().Be(value);
    }

    [Test]
    public void Encrypt_SameInputTwice_ProducesDifferentCiphertext()
    {
        var service = Create();

        // A random per-value nonce means identical plaintext must not yield identical ciphertext.
        service.Encrypt("same").Should().NotBe(service.Encrypt("same"));
    }

    [Test]
    public void Decrypt_TamperedCiphertext_Throws()
    {
        var service = Create();
        var cipher = service.Encrypt("sensitive");
        var bytes = Convert.FromBase64String(cipher);
        bytes[^1] ^= 0xFF; // flip a ciphertext bit — GCM auth tag must reject it
        var tampered = Convert.ToBase64String(bytes);

        var act = () => service.Decrypt(tampered);

        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Decrypt_WithDifferentKey_Throws()
    {
        var cipher = Create().Encrypt("sensitive");

        var act = () => Create("a-completely-different-key").Decrypt(cipher);

        act.Should().Throw<CryptographicException>();
    }

    [Test]
    public void Constructor_EmptyKey_Throws()
    {
        var act = () => Create("");

        act.Should().Throw<ArgumentException>();
    }

    // The key derivation is unchanged: AES key = SHA-256 of the configured key text, so every stored value still decrypts.
    [Test]
    public void AV2ValueWrittenWithTheSha256DerivedKey_StillDecrypts()
    {
        const string plain = "Host=db;Password=kept";
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(Key));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[16];
        using var gcm = new AesGcm(key, 16);
        gcm.Encrypt(nonce, plainBytes, cipherBytes, tag);
        var payload = new byte[] { 0x01 }.Concat(nonce).Concat(tag).Concat(cipherBytes).ToArray();

        Create().Decrypt(Convert.ToBase64String(payload)).Should().Be(plain);
    }

    [Test]
    public void ALegacyV1Value_StillDecrypts()
    {
        const string plain = "Host=db;Password=legacy";

        Create().Decrypt(LegacyV1(plain, Key)).Should().Be(plain);
    }

    [Test]
    public void AValueWrittenWithAPreviousKey_DecryptsThroughIt_InBothFormats()
    {
        const string previousKey = "the key before the rotation";
        var service = new EncryptionService(Key, [previousKey]);

        service.Decrypt(new EncryptionService(previousKey).Encrypt("v2 value")).Should().Be("v2 value");
        service.Decrypt(LegacyV1("v1 value", previousKey)).Should().Be("v1 value");
        service.Decrypt(Create().Encrypt("current value")).Should().Be("current value");
    }

    [Test]
    public void NewValues_AreWrittenWithTheConfiguredKey_NeverAPreviousOne()
    {
        const string previousKey = "the key before the rotation";
        var cipher = new EncryptionService(Key, [previousKey]).Encrypt("fresh");

        Create().Decrypt(cipher).Should().Be("fresh");
        var withThePreviousKey = () => new EncryptionService(previousKey).Decrypt(cipher);
        withThePreviousKey.Should().Throw<CryptographicException>();
    }

    [Test]
    public void AValueNoConfiguredKeyDecrypts_StillThrows()
    {
        var foreign = new EncryptionService("a key nobody configured").Encrypt("secret");
        var foreignLegacy = LegacyV1("secret", "a key nobody configured");
        var service = new EncryptionService(Key, ["the key before the rotation"]);

        var v2 = () => service.Decrypt(foreign);
        var v1 = () => service.Decrypt(foreignLegacy);

        v2.Should().Throw<CryptographicException>();
        v1.Should().Throw<CryptographicException>();
    }

    // A legacy CBC value whose first ciphertext byte happens to be the v2 marker (0x01) is not a v2 value: when the GCM
    // tag check fails, it is read as legacy v1.
    [Test]
    public void ALegacyV1ValueThatStartsLikeAV2Value_StillDecrypts()
    {
        var (plain, cipher) = LegacyV1StartingWithTheV2Marker(Key);

        Convert.FromBase64String(cipher)[0].Should().Be(0x01);
        Create().Decrypt(cipher).Should().Be(plain);
        new EncryptionService(Key, ["the key before the rotation"]).Decrypt(cipher).Should().Be(plain);
        new EncryptionService("the key after the rotation", [Key]).Decrypt(cipher).Should().Be(plain, "through a previous key too");
    }

    // A wrong key leaves a legacy CBC value's padding intact about once in 256 values; what it yields then is not UTF-8,
    // and is refused, so the next configured key reads the value instead of the garbage being returned.
    [Test]
    public void ALegacyValueAWrongKeyDecryptsWithValidPadding_IsReadWithThePreviousKeyThatWroteIt()
    {
        const string previousKey = "the key before the rotation";
        var (plain, cipher) = LegacyV1WithValidPaddingUnderAnotherKey(writtenWith: previousKey, wrongKey: Key);
        var service = new EncryptionService(Key, [previousKey]);

        service.Decrypt(cipher).Should().Be(plain);
        service.Identify(cipher).Should().Be(EncryptionService.KeyMatch.Previous);
        var currentKeyOnly = () => Create().Decrypt(cipher);
        currentKeyOnly.Should().Throw<CryptographicException>();
    }

    [Test]
    public void WhichKeyDecryptsAValue_IsTold()
    {
        const string previousKey = "the key before the rotation";
        var service = new EncryptionService(Key, [previousKey]);

        service.Identify(service.Encrypt("current")).Should().Be(EncryptionService.KeyMatch.Current);
        service.Identify(LegacyV1("current legacy", Key)).Should().Be(EncryptionService.KeyMatch.Current);
        service.Identify(new EncryptionService(previousKey).Encrypt("old")).Should().Be(EncryptionService.KeyMatch.Previous);
        service.Identify(LegacyV1("old legacy", previousKey)).Should().Be(EncryptionService.KeyMatch.Previous);
        service.Identify(new EncryptionService("a key nobody configured").Encrypt("foreign")).Should().Be(EncryptionService.KeyMatch.None);
        service.Identify("not base64 at all").Should().Be(EncryptionService.KeyMatch.None);
        service.Identify(string.Empty).Should().Be(EncryptionService.KeyMatch.Current);
    }

    [Test]
    public void AValueAPreviousKeyDecrypts_IsLoggedOncePerKey_ByItsPositionOnly()
    {
        const string first = "the first previous key";
        const string second = "the second previous key";
        var logs = new Beacon.Tests.Common.LogRecorder();
        var service = new EncryptionService(Key, ["", first, second], logs.For<EncryptionService>());

        service.Decrypt(new EncryptionService(second).Encrypt("a"));
        service.Decrypt(new EncryptionService(second).Encrypt("b"));
        service.Decrypt(new EncryptionService(first).Encrypt("c"));
        service.Decrypt(service.Encrypt("d"));

        logs.Entries.Select(x => x.Message).Should().Equal(
            "A stored value was decrypted with Beacon:PreviousEncryptionKeys entry 2, not the current key; it moves to the current key when it is next saved or re-encrypted. Logged once per key",
            "A stored value was decrypted with Beacon:PreviousEncryptionKeys entry 1, not the current key; it moves to the current key when it is next saved or re-encrypted. Logged once per key");
        logs.Entries.Should().OnlyContain(x => x.Level == Microsoft.Extensions.Logging.LogLevel.Information);
        logs.Entries.Should().NotContain(x => x.Message.Contains(first) || x.Message.Contains(second));
    }

    [Test]
    public void WhetherAValueIsInTheLegacyFormat_IsTold_WhicheverKeyDecryptsIt()
    {
        const string previousKey = "the key configured before";
        var service = new EncryptionService(Key, [previousKey]);

        service.Identify(LegacyV1("current legacy", Key), out var currentLegacy).Should().Be(EncryptionService.KeyMatch.Current);
        currentLegacy.Should().BeTrue();
        service.Identify(LegacyV1("old legacy", previousKey), out var previousLegacy).Should().Be(EncryptionService.KeyMatch.Previous);
        previousLegacy.Should().BeTrue();
        service.Identify(service.Encrypt("current"), out var current).Should().Be(EncryptionService.KeyMatch.Current);
        current.Should().BeFalse();
        service.Identify(new EncryptionService(previousKey).Encrypt("old"), out var previous).Should().Be(EncryptionService.KeyMatch.Previous);
        previous.Should().BeFalse();
    }

    [Test]
    public void ALegacyFormatValue_IsLoggedOnce_WithoutTheValue()
    {
        var logs = new Beacon.Tests.Common.LogRecorder();
        var service = new EncryptionService(Key, [], logs.For<EncryptionService>());

        service.Decrypt(LegacyV1("Host=db;Password=first", Key)).Should().Be("Host=db;Password=first");
        service.Decrypt(LegacyV1("Host=db;Password=second", Key));
        service.Decrypt(service.Encrypt("current"));

        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(Microsoft.Extensions.Logging.LogLevel.Warning);
        entry.Message.Should().Contain("legacy unauthenticated format").And.Contain("Beacon:Encryption:AllowLegacyCbc");
        entry.Message.Should().NotContain("Password");
    }

    [Test]
    public void WithTheLegacyFormatOff_ALegacyValueIsRefused_UnderAnyKey_AndCurrentValuesStillRead()
    {
        const string previousKey = "the key configured before";
        var service = new EncryptionService(Key, [previousKey], allowLegacyFormat: false);
        var (_, startingLikeV2) = LegacyV1StartingWithTheV2Marker(Key);

        var current = () => service.Decrypt(LegacyV1("legacy", Key));
        var previous = () => service.Decrypt(LegacyV1("legacy", previousKey));
        var markerLike = () => service.Decrypt(startingLikeV2);

        current.Should().Throw<CryptographicException>();
        previous.Should().Throw<CryptographicException>();
        markerLike.Should().Throw<CryptographicException>();
        service.Identify(LegacyV1("legacy", Key)).Should().Be(EncryptionService.KeyMatch.None);
        service.Decrypt(service.Encrypt("current")).Should().Be("current");
        service.Decrypt(new EncryptionService(previousKey).Encrypt("old")).Should().Be("old");
    }

    private static (string Plain, string Cipher) LegacyV1StartingWithTheV2Marker(string configuredKey)
    {
        for (var i = 0; i < 100_000; i++)
        {
            // The plaintext's first block decides the first ciphertext block: vary it.
            var plain = $"{i:D8};Host=db;Password=legacy";
            var cipher = LegacyV1(plain, configuredKey);
            if (Convert.FromBase64String(cipher)[0] == 0x01)
            {
                return (plain, cipher);
            }
        }

        throw new InvalidOperationException("No legacy value starting with 0x01 was found.");
    }

    // A legacy value written with one key that another key decrypts with valid PKCS7 padding, to bytes that are not UTF-8.
    private static (string Plain, string Cipher) LegacyV1WithValidPaddingUnderAnotherKey(string writtenWith, string wrongKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(wrongKey));
        using var aes = Aes.Create();
        aes.Key = key;
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        for (var i = 0; i < 100_000; i++)
        {
            var plain = $"{i:D8};Host=db;Password=legacy";
            var cipher = LegacyV1(plain, writtenWith);
            byte[] garbage;
            try
            {
                garbage = aes.DecryptCbc(Convert.FromBase64String(cipher), key[..16]);
            }
            catch (CryptographicException)
            {
                continue;
            }

            try
            {
                strict.GetString(garbage);
            }
            catch (DecoderFallbackException)
            {
                return (plain, cipher);
            }
        }

        throw new InvalidOperationException("No legacy value with valid padding under the other key was found.");
    }

    // What the pre-GCM service wrote: AES-CBC, PKCS7, the SHA-256 key and its first 16 bytes as the IV.
    internal static string LegacyV1(string plain, string configuredKey)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(configuredKey));
        using var aes = Aes.Create();
        aes.Key = key;

        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plain), key[..16]));
    }
}
