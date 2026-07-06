using System.Security.Cryptography;
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
}
