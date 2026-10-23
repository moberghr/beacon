using System.Security.Cryptography;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A successful password sign-in replaces a hash written with weaker parameters by one at the current iteration count,
/// for the same password, without ending the user's sessions; a failed sign-in, a current hash, or a hash that changed
/// in the meantime is left as it is.
/// </summary>
[TestFixture]
public class PasswordRehashTests
{
    private const string Password = "Correct-Horse-1";

    [Test]
    public async Task ASuccessfulSignIn_ReplacesAnOlderHash_WithoutEndingSessions()
    {
        var user = User(UnversionedHash(Password));
        var hasher = new PasswordHasher(NullLogger<PasswordHasher>.Instance);

        var result = await Service([user], hasher).AuthenticateInternalUserAsync("ana", Password);

        result.Success.Should().BeTrue();
        user.PasswordHash.Should().StartWith($"v2${PasswordHasher.Iterations}$");
        hasher.VerifyPassword(Password, user.PasswordHash!, user.PasswordSalt!).Should().BeTrue();
        user.SecurityStamp.Should().Be("stamp");
    }

    [Test]
    public async Task AFailedSignIn_LeavesTheOlderHash()
    {
        var stored = UnversionedHash(Password);
        var user = User(stored);

        var result = await Service([user], new PasswordHasher(NullLogger<PasswordHasher>.Instance)).AuthenticateInternalUserAsync("ana", "wrong");

        result.Success.Should().BeFalse();
        user.PasswordHash.Should().Be(stored.Hash);
        user.PasswordSalt.Should().Be(stored.Salt);
    }

    [Test]
    public async Task ACurrentHash_IsNotRewritten()
    {
        var hasher = new PasswordHasher(NullLogger<PasswordHasher>.Instance);
        var stored = hasher.HashPassword(Password);
        var user = User(stored);

        var result = await Service([user], hasher).AuthenticateInternalUserAsync("ana", Password);

        result.Success.Should().BeTrue();
        user.PasswordHash.Should().Be(stored.hash);
        user.PasswordSalt.Should().Be(stored.salt);
    }

    [Test]
    public async Task AHashChangedSinceTheSignInReadIt_IsNotOverwritten()
    {
        var user = User(UnversionedHash(Password));
        var hasher = new ChangingWhileHashing(user);

        var result = await Service([user], hasher).AuthenticateInternalUserAsync("ana", Password);

        result.Success.Should().BeTrue();
        user.PasswordHash.Should().Be("changed-hash", "a password changed in the meantime is never replaced by the old one");
        user.PasswordSalt.Should().Be("changed-salt");
    }

    [Test]
    public async Task AnUnknownUser_PaysForAVerificationAtTheCurrentIterationCount()
    {
        var hasher = new Mock<IPasswordHasher>();

        await Service([], hasher.Object).AuthenticateInternalUserAsync("nobody", Password);

        hasher.Verify(
            x => x.VerifyPassword(Password, It.Is<string>(y => y.StartsWith($"v2${PasswordHasher.Iterations}$")), It.IsAny<string>()),
            Times.Once);
    }

    private static BeaconUser User((string Hash, string Salt) credential)
    {
        return new BeaconUser
        {
            Id = 1,
            ExternalId = "ext-1",
            UserName = "ana",
            IsInternalUser = true,
            IsEnabled = true,
            PasswordHash = credential.Hash,
            PasswordSalt = credential.Salt,
            SecurityStamp = "stamp"
        };
    }

    private static (string Hash, string Salt) UnversionedHash(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(32);
        var bytes = Rfc2898DeriveBytes.Pbkdf2(password, saltBytes, 100_000, HashAlgorithmName.SHA256, 32);

        return (Convert.ToBase64String(bytes), Convert.ToBase64String(saltBytes));
    }

    private static UserManagementService Service(List<BeaconUser> users, IPasswordHasher hasher)
    {
        var saved = new List<object>();
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return new UserManagementService(factory.Object, hasher, Mock.Of<IRoleService>(), new BeaconConfiguration(), TimeProvider.System, new RecordingSecurityAudit());
    }

    // The real hasher, but the stored credential changes (a concurrent password change) while the new hash is computed.
    private sealed class ChangingWhileHashing(BeaconUser user) : IPasswordHasher
    {
        private readonly PasswordHasher _inner = new(NullLogger<PasswordHasher>.Instance);

        public (string hash, string salt) HashPassword(string password)
        {
            var result = _inner.HashPassword(password);
            user.PasswordHash = "changed-hash";
            user.PasswordSalt = "changed-salt";

            return result;
        }

        public bool VerifyPassword(string password, string hash, string salt) => _inner.VerifyPassword(password, hash, salt);

        public bool NeedsRehash(string hash) => _inner.NeedsRehash(hash);
    }
}
