using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Every role assignment records who made it (<c>AssignedByUserId</c>, the assigner's external id): creating an internal or
/// an external user, and assigning a role. A role Beacon grants itself, the configured default role at a first single
/// sign-on or MCP sign-in, records no assigner. Assigning or removing a role also gives the user a new security stamp, which
/// ends their sessions. (Changing roles, <c>SetUserRolesHandler</c>, is covered with the user administration rules.)
/// </summary>
[TestFixture]
public class RoleGrantProvenanceTests
{
    private const int EditorRole = 2;
    private const int ViewerRole = 3;

    private List<BeaconUser> _users = null!;
    private List<BeaconRole> _roles = null!;
    private List<BeaconUserRole> _userRoles = null!;
    private List<object> _saved = null!;

    [SetUp]
    public void SetUp()
    {
        _users = [new BeaconUser { Id = 1, ExternalId = "ext-root", UserName = "root", IsSuperAdmin = true, IsEnabled = true }];
        _roles =
        [
            new BeaconRole { Id = EditorRole, Name = "Editor", Level = 2, IsSystemRole = true },
            new BeaconRole { Id = ViewerRole, Name = "Viewer", Level = 1, IsSystemRole = true }
        ];
        _userRoles = [];
        _saved = [];
    }

    [Test]
    public async Task CreatingAnInternalUser_RecordsTheCreatorOnEveryRole()
    {
        var response = await Service().CreateInternalUserAsync(new CreateInternalUserRequest
        {
            UserName = "newbie",
            Password = "Aa1!aaaaaaaa",
            RoleIds = [EditorRole, ViewerRole],
            AssignedByUserId = "ext-admin"
        });

        response.Success.Should().BeTrue();
        _saved.OfType<BeaconUser>().Should().ContainSingle().Which.UserRoles
            .Should().HaveCount(2).And.OnlyContain(x => x.AssignedByUserId == "ext-admin");
    }

    [Test]
    public async Task PreRegisteringAnExternalUser_RecordsTheCreatorOnEveryRole()
    {
        var response = await Service().CreateExternalUserAsync(new CreateExternalUserRequest
        {
            ExternalId = "sub-new",
            UserName = "newbie",
            RoleIds = [ViewerRole],
            AssignedByUserId = "ext-useradmin"
        });

        response.Success.Should().BeTrue();
        _saved.OfType<BeaconUser>().Should().ContainSingle().Which.UserRoles
            .Should().ContainSingle().Which.AssignedByUserId.Should().Be("ext-useradmin");
    }

    [Test]
    public async Task TheDefaultRoleAtAFirstSignIn_IsGrantedByBeacon_WithNoAssigner()
    {
        var user = await Service().GetOrCreateExternalUserAsync("sub-new", "https://idp.example.test/", "newbie", null, null, "Viewer");

        user.Roles.Should().ContainSingle().Which.Name.Should().Be("Viewer");
        _saved.OfType<BeaconUser>().Should().ContainSingle().Which.UserRoles
            .Should().ContainSingle().Which.AssignedByUserId.Should().BeNull();
    }

    [Test]
    public async Task AssigningARole_RecordsWhoAssignedIt()
    {
        var response = await ServiceFindingByKey().AssignRoleAsync(1, ViewerRole, "ext-admin");

        response.Success.Should().BeTrue();
        _saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.AssignedByUserId.Should().Be("ext-admin");
    }

    // A role change ends every session of the user (a new security stamp, in the same save); a refused one changes nothing.
    [Test]
    public async Task AssigningARole_SignsOutEverySessionOfTheUser_AndAssigningOneTheyHoldDoesNot()
    {
        var user = _users[0];
        user.SecurityStamp = "before";
        _userRoles.Add(new BeaconUserRole { UserId = 1, RoleId = EditorRole });

        var held = await ServiceFindingByKey().AssignRoleAsync(1, EditorRole, "ext-admin");
        var unchanged = user.SecurityStamp;
        var assigned = await ServiceFindingByKey().AssignRoleAsync(1, ViewerRole, "ext-admin");

        held.Success.Should().BeFalse();
        unchanged.Should().Be("before");
        assigned.Success.Should().BeTrue();
        user.SecurityStamp.Should().NotBeNullOrEmpty().And.NotBe("before");
    }

    [Test]
    public async Task RemovingARole_SignsOutEverySessionOfTheUser_AndRemovingOneTheyLackDoesNot()
    {
        var user = _users[0];
        user.SecurityStamp = "before";
        _userRoles.Add(new BeaconUserRole { UserId = 1, RoleId = ViewerRole });

        var lacked = await Service().RemoveRoleAsync(1, EditorRole);
        var unchanged = user.SecurityStamp;
        var removed = await Service().RemoveRoleAsync(1, ViewerRole);

        lacked.Success.Should().BeFalse();
        unchanged.Should().Be("before");
        removed.Success.Should().BeTrue();
        _userRoles.Should().BeEmpty();
        user.SecurityStamp.Should().NotBeNullOrEmpty().And.NotBe("before");
    }

    // The service with users and roles found by their key, as AssignRoleAsync looks them up.
    private UserManagementService ServiceFindingByKey()
    {
        var userSet = RecordingBeaconContext.MemorySet(_users, _saved);
        Mock.Get(userSet)
            .Setup(x => x.FindAsync(It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns<object[], CancellationToken>((keys, _) => ValueTask.FromResult(_users.FirstOrDefault(x => x.Id == (int)keys[0])));
        var roleSet = RecordingBeaconContext.MemorySet(_roles, _saved);
        Mock.Get(roleSet)
            .Setup(x => x.FindAsync(It.IsAny<object[]>(), It.IsAny<CancellationToken>()))
            .Returns<object[], CancellationToken>((keys, _) => ValueTask.FromResult(_roles.FirstOrDefault(x => x.Id == (int)keys[0])));

        return Service(userSet, roleSet);
    }

    private UserManagementService Service(DbSet<BeaconUser>? userSet = null, DbSet<BeaconRole>? roleSet = null)
    {
        var hasher = new Mock<IPasswordHasher>();
        hasher
            .Setup(x => x.HashPassword(It.IsAny<string>()))
            .Returns(("hash", "salt"));
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = userSet ?? RecordingBeaconContext.MemorySet(_users, _saved),
            [typeof(BeaconRole)] = roleSet ?? RecordingBeaconContext.MemorySet(_roles, _saved),
            [typeof(BeaconUserRole)] = RecordingBeaconContext.MemorySet(_userRoles, _saved)
        };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, _saved));

        return new UserManagementService(factory.Object, hasher.Object, Mock.Of<IRoleService>(), new BeaconConfiguration(), TimeProvider.System);
    }
}
