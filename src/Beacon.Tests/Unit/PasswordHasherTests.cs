using System.Security.Cryptography;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// PBKDF2-HMAC-SHA256 with the iteration count written into the hash (<c>v2$iterations$hash</c>): a hash written with
/// fewer iterations than the current count, or in the earlier unversioned format, still verifies and asks to be
/// replaced; a malformed one fails closed.
/// </summary>
[TestFixture]
public class PasswordHasherTests
{
    private const string Password = "Correct-Horse-1";

    private readonly PasswordHasher _hasher = new(NullLogger<PasswordHasher>.Instance);

    [Test]
    public void AHash_CarriesTheCurrentIterationCount_AndVerifies()
    {
        var (hash, salt) = _hasher.HashPassword(Password);

        hash.Should().StartWith("v2$600000$");
        PasswordHasher.Iterations.Should().Be(600_000);
        _hasher.VerifyPassword(Password, hash, salt).Should().BeTrue();
        _hasher.VerifyPassword(Password + "x", hash, salt).Should().BeFalse();
        _hasher.NeedsRehash(hash).Should().BeFalse();
    }

    [Test]
    public void AnUnversionedHash_Verifies_AndAsksToBeReplaced()
    {
        var (hash, salt) = UnversionedHash(Password);

        _hasher.VerifyPassword(Password, hash, salt).Should().BeTrue();
        _hasher.VerifyPassword(Password + "x", hash, salt).Should().BeFalse();
        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    [Test]
    public void AVersionedHashWithFewerIterations_Verifies_AndAsksToBeReplaced()
    {
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(Password, saltBytes, 200_000, HashAlgorithmName.SHA256, 32);
        var hash = "v2$200000$" + Convert.ToBase64String(bytes);
        var salt = Convert.ToBase64String(saltBytes);

        _hasher.VerifyPassword(Password, hash, salt).Should().BeTrue();
        _hasher.NeedsRehash(hash).Should().BeTrue();
    }

    [TestCase("v2$abc$AAAA")]
    [TestCase("v2$600000")]
    [TestCase("v2$600000$not base64!")]
    [TestCase("v2$1000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [TestCase("v2$2000000000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [TestCase("v2$+600000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [TestCase("v9$600000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [TestCase("not base64!")]
    public void AMalformedHash_FailsClosed_AndIsNotReplaced(string hash)
    {
        var logs = new LogRecorder();
        var hasher = new PasswordHasher(logs.For<PasswordHasher>());
        var salt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

        hasher.VerifyPassword(Password, hash, salt).Should().BeFalse();
        hasher.NeedsRehash(hash).Should().BeFalse();
        logs.Contains("malformed").Should().BeTrue();
    }

    [Test]
    public void TheUnmatchableHash_IsInTheCurrentFormat()
    {
        var hash = PasswordHasher.UnmatchableHash();

        hash.Should().StartWith("v2$600000$");
        _hasher.NeedsRehash(hash).Should().BeFalse();
        _hasher.VerifyPassword(Password, hash, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))).Should().BeFalse();
    }

    [Test]
    public void EveryVerification_PerformsTheCurrentIterationCount()
    {
        var current = _hasher.HashPassword(Password);
        var unversioned = UnversionedHash(Password);
        var fewerIterations = VersionedHash(Password, 200_000);
        var randomSalt = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var cases = new (string Name, string Password, string Hash, string Salt)[]
        {
            ("current, right password", Password, current.hash, current.salt),
            ("current, wrong password", "wrong", current.hash, current.salt),
            ("unversioned, right password", Password, unversioned.Hash, unversioned.Salt),
            ("unversioned, wrong password", "wrong", unversioned.Hash, unversioned.Salt),
            ("fewer iterations, wrong password", "wrong", fewerIterations.Hash, fewerIterations.Salt),
            ("unmatchable", "wrong", PasswordHasher.UnmatchableHash(), randomSalt),
            ("malformed hash", "wrong", "not base64!", randomSalt),
            ("too few iterations", "wrong", "v2$1000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", randomSalt),
            ("too many iterations", "wrong", "v2$2000000000$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=", randomSalt),
            ("malformed salt", "wrong", current.hash, "not base64!"),
            ("no stored hash", "wrong", string.Empty, randomSalt),
            ("no stored salt", "wrong", current.hash, string.Empty)
        };

        foreach (var (name, password, hash, salt) in cases)
        {
            var iterations = 0L;
            var hasher = new PasswordHasher(NullLogger<PasswordHasher>.Instance, (x, y, z) =>
            {
                iterations += z;
                return Rfc2898DeriveBytes.Pbkdf2(x, y, z, HashAlgorithmName.SHA256, 32);
            });

            hasher.VerifyPassword(password, hash, salt);

            iterations.Should().Be(PasswordHasher.Iterations, name);
        }
    }

    private static (string Hash, string Salt) VersionedHash(string password, int iterations)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, iterations, HashAlgorithmName.SHA256, 32);

        return ($"v2${iterations}$" + Convert.ToBase64String(bytes), Convert.ToBase64String(saltBytes));
    }

    private static (string Hash, string Salt) UnversionedHash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 100_000, HashAlgorithmName.SHA256, 32);

        return (Convert.ToBase64String(bytes), Convert.ToBase64String(saltBytes));
    }
}
