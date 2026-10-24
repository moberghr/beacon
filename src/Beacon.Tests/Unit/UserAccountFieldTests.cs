using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The user name and e-mail address an administrator or first-run setup sets: a user name of 3 to 100 characters without
/// surrounding whitespace or control characters, and a plain e-mail address or none. The handlers trim what they are sent;
/// the service refuses what is still invalid. An update checks only the fields it changes.
/// </summary>
[TestFixture]
public class UserAccountFieldTests
{
    private const string Password = "Aa1!aaaaaaaa";

    private List<BeaconUser> _users = null!;
    private List<BeaconRole> _roles = null!;
    private List<object> _saved = null!;

    [SetUp]
    public void SetUp()
    {
        _users = [];
        _roles = [];
        _saved = [];
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("ab")]
    [TestCase(" padded ")]
    [TestCase("tab\there")]
    [TestCase("bell\u0007name")]
    public async Task CreatingAUser_RefusesAnInvalidUserName(string userName)
    {
        var internalUser = await Service().CreateInternalUserAsync(new CreateInternalUserRequest { UserName = userName, Password = Password });
        var externalUser = await Service().CreateExternalUserAsync(new CreateExternalUserRequest { ExternalId = "sub-1", UserName = userName });

        internalUser.Success.Should().BeFalse();
        internalUser.Message.Should().Contain("user name");
        externalUser.Success.Should().BeFalse();
        externalUser.Message.Should().Contain("user name");
        _saved.Should().BeEmpty();
    }

    [Test]
    public async Task CreatingAUser_RefusesAUserNameLongerThanTheColumn()
    {
        var response = await Service().CreateInternalUserAsync(new CreateInternalUserRequest { UserName = new string('a', 101), Password = Password });

        response.Success.Should().BeFalse();
        response.Message.Should().Contain("100");
        _saved.Should().BeEmpty();
    }

    [TestCase("not-an-email")]
    [TestCase("Ana <ana@example.test>")]
    [TestCase("ana@example.test, ben@example.test")]
    [TestCase(" ana@example.test")]
    public async Task CreatingAUser_RefusesAnInvalidEmail(string email)
    {
        var internalUser = await Service().CreateInternalUserAsync(new CreateInternalUserRequest { UserName = "ana", Email = email, Password = Password });
        var externalUser = await Service().CreateExternalUserAsync(new CreateExternalUserRequest { ExternalId = "sub-1", UserName = "ana", Email = email });

        internalUser.Success.Should().BeFalse();
        internalUser.Message.Should().Contain("e-mail");
        externalUser.Success.Should().BeFalse();
        externalUser.Message.Should().Contain("e-mail");
        _saved.Should().BeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("ana@example.test")]
    public async Task CreatingAUser_AcceptsAPlainEmailOrNone(string? email)
    {
        var response = await Service().CreateInternalUserAsync(new CreateInternalUserRequest { UserName = "ana", Email = email, Password = Password });

        response.Success.Should().BeTrue();
        _saved.OfType<BeaconUser>().Should().ContainSingle().Which.UserName.Should().Be("ana");
    }

    [Test]
    public async Task TheHandlers_SendTrimmedValues()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.UpdateUserAsync(It.IsAny<UpdateUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BaseResponse { Success = true });
        var rules = new UserAdministrationRules(
            Factory(),
            TestProjectAccess.Actors(1, "ext-admin"),
            TestPermissions.Admin,
            Options.Create(new RoleMappingOptions()),
            new LogRecorder().For<UserAdministrationRules>());

        await new UpdateUserHandler(users.Object, rules)
            .Handle(new UpdateUserCommand(2, "  ana  ", "  ", "Ana", true), CancellationToken.None);

        users.Verify(
            x => x.UpdateUserAsync(It.Is<UpdateUserRequest>(y => y.UserName == "ana" && y.Email == null), It.IsAny<CancellationToken>()),
            Times.Once);

        users
            .Setup(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BaseResponse { Success = true });
        users
            .Setup(x => x.CreateExternalUserAsync(It.IsAny<CreateExternalUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BaseResponse { Success = true });

        await new CreateInternalUserHandler(users.Object, rules, TestProjectAccess.Actors(1, "ext-admin"))
            .Handle(new CreateInternalUserCommand(" ben ", " ben@example.test ", null, Password, []), CancellationToken.None);
        await new CreateExternalUserHandler(users.Object, rules, TestProjectAccess.Actors(1, "ext-admin"))
            .Handle(new CreateExternalUserCommand("sub-1", "\tcyd ", "", null, []), CancellationToken.None);

        users.Verify(
            x => x.CreateInternalUserAsync(It.Is<CreateInternalUserRequest>(y => y.UserName == "ben" && y.Email == "ben@example.test"), It.IsAny<CancellationToken>()),
            Times.Once);
        users.Verify(
            x => x.CreateExternalUserAsync(It.Is<CreateExternalUserRequest>(y => y.UserName == "cyd" && y.Email == null), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Test]
    public async Task UpdatingAUser_RefusesAnInvalidNewUserNameOrEmail()
    {
        var user = StoredUser("ana", "ana@example.test");

        var shortName = await Service().UpdateUserAsync(Update(user, userName: "an"));
        var badEmail = await Service().UpdateUserAsync(Update(user, email: "ana-at-example"));

        shortName.Success.Should().BeFalse();
        shortName.Message.Should().Contain("user name");
        badEmail.Success.Should().BeFalse();
        badEmail.Message.Should().Contain("e-mail");
        user.UserName.Should().Be("ana");
        user.Email.Should().Be("ana@example.test");
    }

    [Test]
    public async Task UpdatingAUser_KeepsAStoredNameAndEmailItDoesNotChange()
    {
        // Written before these rules: an account saved again unchanged is not refused for them.
        var user = StoredUser("x", "legacy-address");

        var response = await Service().UpdateUserAsync(Update(user, displayName: "Renamed"));

        response.Success.Should().BeTrue();
        user.DisplayName.Should().Be("Renamed");
    }

    [Test]
    public async Task FirstRunSetup_RefusesAConfirmationThatDiffers_AndAnInvalidUserName()
    {
        var mismatch = () => Service().CreateSuperAdminAsync(new CreateSuperAdminRequest { UserName = "admin", Password = Password, ConfirmPassword = Password + "x" });
        var shortName = () => Service().CreateSuperAdminAsync(new CreateSuperAdminRequest { UserName = " ab ", Password = Password, ConfirmPassword = Password });
        var badEmail = () => Service().CreateSuperAdminAsync(new CreateSuperAdminRequest { UserName = "admin", Email = "admin", Password = Password, ConfirmPassword = Password });

        await mismatch.Should().ThrowAsync<BeaconException>().WithMessage("*do not match*");
        await shortName.Should().ThrowAsync<BeaconException>().WithMessage("*user name*");
        await badEmail.Should().ThrowAsync<BeaconException>().WithMessage("*e-mail*");
        _saved.Should().BeEmpty();
    }

    [Test]
    public async Task FirstRunSetup_StoresTheTrimmedUserNameAndEmail()
    {
        _roles.Add(new BeaconRole { Id = 1, Name = RoleService.RoleNames.Admin, Level = 3, IsSystemRole = true });

        await Service().CreateSuperAdminAsync(new CreateSuperAdminRequest { UserName = "  admin ", Email = " admin@example.test ", Password = Password, ConfirmPassword = Password });

        var created = _saved.OfType<BeaconUser>().Should().ContainSingle().Subject;
        created.UserName.Should().Be("admin");
        created.Email.Should().Be("admin@example.test");
        created.DisplayName.Should().Be("admin");
    }

    private BeaconUser StoredUser(string userName, string? email)
    {
        var user = new BeaconUser { Id = 2, ExternalId = "ext-2", UserName = userName, Email = email, DisplayName = "Ana", IsEnabled = true, SecurityStamp = "stamp" };
        _users.Add(user);

        return user;
    }

    private static UpdateUserRequest Update(BeaconUser user, string? userName = null, string? email = null, string? displayName = null)
    {
        return new UpdateUserRequest
        {
            UserId = user.Id,
            UserName = userName ?? user.UserName,
            Email = email ?? user.Email,
            DisplayName = displayName ?? user.DisplayName,
            IsEnabled = user.IsEnabled
        };
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, _saved),
            [typeof(BeaconRole)] = RecordingBeaconContext.MemorySet(_roles, _saved),
            [typeof(BeaconUserRole)] = RecordingBeaconContext.MemorySet(new List<BeaconUserRole>(), _saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved, new SqlCapture()));

        return factory.Object;
    }

    private UserManagementService Service()
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.HashPassword(It.IsAny<string>()))
            .Returns(("hash", "salt"));

        return new UserManagementService(Factory(), hasher.Object, Mock.Of<IRoleService>(), new BeaconConfiguration(), TimeProvider.System, new RecordingSecurityAudit());
    }
}
