using System.Security.Cryptography;
using System.Text;
using Beacon.Core.Services.Embed;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>A grant secret is <c>bge_</c> and 32 random bytes as base64url; only its SHA-256 is stored.</summary>
[TestFixture]
public class EmbedGrantSecretTests
{
    [Test]
    public void ASecret_HasItsForm_AndItsHashIsItsSha256()
    {
        var (secret, hash) = EmbedGrantSecret.Create();

        secret.Should().MatchRegex("^bge_[A-Za-z0-9_-]{43}$");
        EmbedGrantSecret.IsWellFormed(secret).Should().BeTrue();
        hash.Should().Be(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)))).And.HaveLength(64);
        hash.Should().NotContain(secret[4..]);
        EmbedGrantSecret.Hash(secret).Should().Be(hash);
    }

    [Test]
    public void SecretsAndPublicIds_AreNeverRepeated()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => EmbedGrantSecret.Create().Secret).ToList();
        var publicIds = Enumerable.Range(0, 50).Select(_ => EmbedGrantSecret.NewPublicId()).ToList();

        secrets.Should().OnlyHaveUniqueItems();
        publicIds.Should().OnlyHaveUniqueItems().And.OnlyContain(x => x.Length == 32 && x.All(Uri.IsHexDigit) && x == x.ToLowerInvariant());
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("bge_")]
    [TestCase("sk-sem_0000000000000000000000000000000000000000000")]
    [TestCase("bge_00000000000000000000000000000000000000000+/")]
    [TestCase("bge_000000000000000000000000000000000000000000")]
    [TestCase("BGE_0000000000000000000000000000000000000000000")]
    public void AValueOfAnotherForm_IsNotASecret(string? value)
    {
        EmbedGrantSecret.IsWellFormed(value).Should().BeFalse();
    }
}
