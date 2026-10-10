using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Internal (password) login: only internal users match, deterministically; the password is verified before the account
/// state is looked at (against a dummy credential when there is no account); every failure answers the same.
/// </summary>
[TestFixture]
public class InternalLoginTests
{
    private const string Generic = "Invalid username or password.";

    [Test]
    public async Task UnknownUser_StillPaysForAPasswordCheck_AndGetsTheGenericAnswer()
    {
        var hasher = Hasher(correctHash: "hash-a");

        var result = await Service([], hasher).AuthenticateInternalUserAsync("nobody", "pw");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(Generic);
        hasher.Verify(x => x.VerifyPassword("pw", It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Test]
    public async Task DisabledUser_IsCheckedOnlyAfterThePassword_AndGetsTheGenericAnswer()
    {
        var hasher = Hasher(correctHash: "hash-a");
        var users = new List<BeaconUser> { InternalUser(1, "ana", "hash-a", enabled: false) };

        var wrongPassword = await Service(users, hasher).AuthenticateInternalUserAsync("ana", "wrong");
        var rightPassword = await Service(users, hasher).AuthenticateInternalUserAsync("ana", "right");

        wrongPassword.ErrorMessage.Should().Be(Generic);
        rightPassword.Success.Should().BeFalse();
        rightPassword.ErrorMessage.Should().Be(Generic, "a disabled account is indistinguishable from a wrong password");
        hasher.Verify(x => x.VerifyPassword(It.IsAny<string>(), "hash-a", "salt"), Times.Exactly(2));
    }

    [Test]
    public async Task ExternalUserWithTheSameName_NeverMatches()
    {
        var hasher = Hasher(correctHash: "hash-a");
        var external = InternalUser(1, "ana", "hash-a", enabled: true);
        external.IsInternalUser = false;

        var result = await Service([external], hasher).AuthenticateInternalUserAsync("ana", "right");

        result.Success.Should().BeFalse();
        hasher.Verify(x => x.VerifyPassword("right", "hash-a", "salt"), Times.Never);
    }

    [Test]
    public async Task ExactUserName_WinsOverAnotherUsersEmail()
    {
        var hasher = Hasher(correctHash: "hash-ana");
        var users = new List<BeaconUser>
        {
            InternalUser(1, "mallory", "hash-mallory", enabled: true, email: "ana"),
            InternalUser(2, "ana", "hash-ana", enabled: true)
        };

        var result = await Service(users, hasher).AuthenticateInternalUserAsync("ana", "right");

        result.Success.Should().BeTrue();
        result.User!.UserName.Should().Be("ana");
    }

    [Test]
    public async Task SharedEmail_ResolvesToTheOldestAccount()
    {
        var hasher = Hasher(correctHash: "hash-first");
        var users = new List<BeaconUser>
        {
            InternalUser(8, "second", "hash-second", enabled: true, email: "team@example.com"),
            InternalUser(3, "first", "hash-first", enabled: true, email: "team@example.com")
        };

        var result = await Service(users, hasher).AuthenticateInternalUserAsync("team@example.com", "right");

        result.Success.Should().BeTrue();
        result.User!.UserName.Should().Be("first");
    }

    [Test]
    public async Task EnabledUser_WithTheRightPassword_SignsInWithTheirRoles()
    {
        var hasher = Hasher(correctHash: "hash-a");
        var user = InternalUser(1, "ana", "hash-a", enabled: true);
        user.UserRoles = [new BeaconUserRole { Role = new BeaconRole { Name = "Editor", Level = 2 } }];

        var result = await Service([user], hasher).AuthenticateInternalUserAsync("ana", "right");

        result.Success.Should().BeTrue();
        result.User!.Roles.Should().Equal("Editor");
        user.LastLoginAt.Should().NotBeNull();
    }

    private static Mock<IPasswordHasher> Hasher(string correctHash)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.VerifyPassword(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()))
            .Returns<string, string, string>((password, hash, _) => password == "right" && hash == correctHash);

        return hasher;
    }

    private static BeaconUser InternalUser(int id, string userName, string hash, bool enabled, string? email = null)
    {
        return new BeaconUser
        {
            Id = id,
            ExternalId = Guid.NewGuid().ToString(),
            UserName = userName,
            Email = email,
            IsInternalUser = true,
            IsEnabled = enabled,
            PasswordHash = hash,
            PasswordSalt = "salt"
        };
    }

    private static UserManagementService Service(List<BeaconUser> users, Mock<IPasswordHasher> hasher)
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

        return new UserManagementService(factory.Object, hasher.Object, Mock.Of<IRoleService>(), new BeaconConfiguration(), TimeProvider.System);
    }
}
