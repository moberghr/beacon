using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// A UserAdmin manages users without being able to raise anyone, itself included: it never assigns or removes the Admin
/// role or a functional role, never changes its own account or roles, and never changes an Admin's, a super admin's or a
/// functional role holder's account (403). An Admin assigns any role. Separation of duties, when on, refuses roles that
/// would give one user both permissions of a conflicting pair (400), whoever assigns them. Every role change is logged
/// with ids only.
/// </summary>
[TestFixture]
public class UserAdministrationRulesTests
{
    private const int CallerId = 3;
    private const int BenId = 8;
    private const int AdminUserId = 9;
    private const int AuditorUserId = 10;
    private const int SuperAdminId = 11;

    private const int AdminRole = 1;
    private const int EditorRole = 2;
    private const int ViewerRole = 3;
    private const int ApproverRole = 10;
    private const int AuditorRole = 11;
    private const int SecurityAdminRole = 12;
    private const int UserAdminRole = 13;
    private const int MigrationOperatorRole = 15;

    private List<BeaconRole> _roles = null!;
    private List<BeaconUser> _users = null!;
    private List<BeaconUserRole> _userRoles = null!;
    private List<object> _saved = null!;
    private LogRecorder _logs = null!;

    [SetUp]
    public void SetUp()
    {
        _roles =
        [
            Role(AdminRole, RoleService.RoleNames.Admin, RoleService.RoleLevels.Admin),
            Role(EditorRole, RoleService.RoleNames.Editor, RoleService.RoleLevels.Editor),
            Role(ViewerRole, RoleService.RoleNames.Viewer, RoleService.RoleLevels.Viewer),
            Role(ApproverRole, BeaconPermissions.Approver, RoleService.RoleLevels.Functional),
            Role(AuditorRole, BeaconPermissions.Auditor, RoleService.RoleLevels.Functional),
            Role(SecurityAdminRole, BeaconPermissions.SecurityAdmin, RoleService.RoleLevels.Functional),
            Role(UserAdminRole, BeaconPermissions.UserAdmin, RoleService.RoleLevels.Functional),
            Role(14, BeaconPermissions.DataSourceAdmin, RoleService.RoleLevels.Functional),
            Role(MigrationOperatorRole, BeaconPermissions.MigrationOperator, RoleService.RoleLevels.Functional)
        ];
        _users =
        [
            User(CallerId, "ext-ana"),
            User(BenId, "ext-ben"),
            User(AdminUserId, "ext-boss"),
            User(AuditorUserId, "ext-ola"),
            User(SuperAdminId, "ext-root", isSuperAdmin: true)
        ];
        _userRoles =
        [
            Assigned(CallerId, EditorRole),
            Assigned(CallerId, UserAdminRole),
            Assigned(BenId, ViewerRole),
            Assigned(AdminUserId, AdminRole),
            Assigned(AuditorUserId, AuditorRole)
        ];
        _saved = [];
        _logs = new LogRecorder();
    }

    [Test]
    public async Task AUserAdmin_ChangesAnotherUsersBuiltInRoles_AndTheChangeIsLoggedWithIdsOnly()
    {
        await SetRoles(UserAdmin(), BenId, EditorRole);

        _saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            UserId = BenId,
            RoleId = EditorRole,
            AssignedByUserId = "ext-ana"
        });
        _userRoles.Should().NotContain(x => x.UserId == BenId && x.RoleId == ViewerRole, "the Viewer role was removed");
        var entry = _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("User roles changed", StringComparison.Ordinal)).Subject;
        entry.Level.Should().Be(LogLevel.Information);
        entry.Message.Should().Contain($"user {BenId}").And.Contain($"added roles {EditorRole}").And.Contain($"removed roles {ViewerRole}").And.Contain($"by user {CallerId}");
        entry.Message.Should().NotContain("ext-ben").And.NotContain("Viewer");
    }

    [TestCase(AdminRole)]
    [TestCase(ApproverRole)]
    [TestCase(AuditorRole)]
    [TestCase(SecurityAdminRole)]
    [TestCase(UserAdminRole)]
    [TestCase(MigrationOperatorRole)]
    public async Task AUserAdmin_AssigningTheAdminRoleOrAFunctionalRole_IsForbidden(int roleId)
    {
        var act = () => SetRoles(UserAdmin(), BenId, ViewerRole, roleId);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.ProtectedRoleMessage);
        _saved.Should().BeEmpty();
        _userRoles.Should().Contain(x => x.UserId == BenId && x.RoleId == ViewerRole);
    }

    [Test]
    public async Task AUserAdmin_CannotGrantOrDemoteThemself()
    {
        var selfGrant = () => SetRoles(UserAdmin(), CallerId, EditorRole, UserAdminRole, AdminRole);
        var selfDemotion = () => SetRoles(UserAdmin(), CallerId, UserAdminRole);
        var selfEdit = () => Update(UserAdmin(), CallerId);
        var selfDisable = () => Toggle(UserAdmin(), CallerId);

        await selfGrant.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.OwnAccountMessage);
        await selfDemotion.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.OwnAccountMessage);
        await selfEdit.Should().ThrowAsync<BeaconForbiddenException>();
        await selfDisable.Should().ThrowAsync<BeaconForbiddenException>();
        _saved.Should().BeEmpty();
        _userRoles.Where(x => x.UserId == CallerId).Select(x => x.RoleId).Should().BeEquivalentTo([EditorRole, UserAdminRole]);
    }

    [TestCase(AdminUserId)]
    [TestCase(AuditorUserId)]
    [TestCase(SuperAdminId)]
    public async Task AUserAdmin_CannotChangeAnAdminASuperAdminOrAFunctionalRoleHolder(int userId)
    {
        var roles = () => SetRoles(UserAdmin(), userId, ViewerRole);
        var update = () => Update(UserAdmin(), userId);
        var toggle = () => Toggle(UserAdmin(), userId);

        await roles.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.ProtectedUserMessage);
        await update.Should().ThrowAsync<BeaconForbiddenException>();
        await toggle.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [Test]
    public async Task AUserAdmin_UpdatesAndDisablesOtherUsers()
    {
        var users = new Mock<IUserManagementService>();
        users.Setup(x => x.UpdateUserAsync(It.IsAny<UpdateUserRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BaseResponse { Success = true });
        users.Setup(x => x.ToggleUserEnabledAsync(BenId, It.IsAny<CancellationToken>())).ReturnsAsync(new BaseResponse { Success = true });

        await Update(UserAdmin(), BenId, users);
        await Toggle(UserAdmin(), BenId, users);

        users.Verify(x => x.UpdateUserAsync(It.Is<UpdateUserRequest>(y => y.UserId == BenId), It.IsAny<CancellationToken>()), Times.Once);
        users.Verify(x => x.ToggleUserEnabledAsync(BenId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AnAdmin_AssignsAFunctionalRole_AndChangesItsOwnRoles()
    {
        await SetRoles(Admin(), BenId, ViewerRole, AuditorRole);
        await SetRoles(Admin(), CallerId, EditorRole);

        _saved.OfType<BeaconUserRole>().Should().ContainSingle(x => x.UserId == BenId && x.RoleId == AuditorRole);
        _userRoles.Should().NotContain(x => x.UserId == CallerId && x.RoleId == UserAdminRole);
    }

    [Test]
    public async Task ACallerWithoutTheUserAdminPermissionNow_IsForbidden_WhateverItsSessionClaims()
    {
        var act = () => SetRoles(TestPermissions.None, BenId, EditorRole);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        _saved.Should().BeEmpty();
    }

    [TestCase(ApproverRole, SecurityAdminRole)]
    [TestCase(AuditorRole, SecurityAdminRole)]
    [TestCase(AuditorRole, UserAdminRole)]
    [TestCase(UserAdminRole, ApproverRole)]
    [TestCase(UserAdminRole, MigrationOperatorRole)]
    public async Task SeparationOfDuties_RefusesAConflictingPair_EvenFromAnAdmin(int first, int second)
    {
        var act = () => SetRoles(Admin(), BenId, [first, second], separationOfDuties: true);
        var withoutSeparation = () => SetRoles(Admin(), BenId, [first, second], separationOfDuties: false);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Separation of duties*");
        _saved.Should().BeEmpty();
        await withoutSeparation.Should().NotThrowAsync("separation of duties is off by default");
    }

    [Test]
    public async Task SeparationOfDuties_CountsTheRolesTheUserKeeps()
    {
        // Ola already holds Auditor: adding SecurityAdmin next to it is the conflicting pair.
        var act = () => SetRoles(Admin(), AuditorUserId, [AuditorRole, SecurityAdminRole], separationOfDuties: true);
        var allowed = () => SetRoles(Admin(), AuditorUserId, [AuditorRole, ApproverRole], separationOfDuties: true);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await allowed.Should().NotThrowAsync();
    }

    [Test]
    public async Task AnUnknownRole_IsABadRequest_AndAnUnknownUser_NotFound()
    {
        var unknownRole = () => SetRoles(Admin(), BenId, 999);
        var unknownUser = () => SetRoles(Admin(), 404, ViewerRole);

        await unknownRole.Should().ThrowAsync<InvalidOperationException>().WithMessage("*999*");
        await unknownUser.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task CreatingAUser_WithTheAdminRoleOrAFunctionalRole_IsAnAdminsAlone()
    {
        var users = new Mock<IUserManagementService>();
        users.Setup(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BaseResponse { Success = true });

        var byUserAdmin = () => CreateInternal(UserAdmin(), users, AuditorRole);
        var byUserAdminAsAdmin = () => CreateInternal(UserAdmin(), users, AdminRole);

        await byUserAdmin.Should().ThrowAsync<BeaconForbiddenException>();
        await byUserAdminAsAdmin.Should().ThrowAsync<BeaconForbiddenException>();
        users.Verify(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never, "a refused role creates nobody");

        await CreateInternal(Admin(), users, AuditorRole);

        users.Verify(x => x.CreateInternalUserAsync(It.Is<CreateInternalUserRequest>(y => y.RoleIds.SequenceEqual(new[] { AuditorRole })), It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase(AdminRole)]
    [TestCase(AuditorRole)]
    [TestCase(UserAdminRole)]
    public async Task CreatingAnExternalUser_WithTheAdminRoleOrAFunctionalRole_ByAUserAdmin_IsForbidden_AndCreatesNobody(int roleId)
    {
        var users = ExternalUsers();

        var act = () => CreateExternal(UserAdmin(), users, false, ViewerRole, roleId);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.ProtectedRoleMessage);
        users.Verify(x => x.CreateExternalUserAsync(It.IsAny<CreateExternalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never, "a refused role creates nobody");
    }

    [Test]
    public async Task CreatingAnExternalUser_ByAUserAdminWithABuiltInRole_OrByAnAdminWithAFunctionalRole_CreatesThem()
    {
        var users = ExternalUsers();

        await CreateExternal(UserAdmin(), users, false, ViewerRole);
        await CreateExternal(Admin(), users, false, AuditorRole);

        users.Verify(x => x.CreateExternalUserAsync(It.Is<CreateExternalUserRequest>(y => y.RoleIds.SequenceEqual(new[] { ViewerRole })), It.IsAny<CancellationToken>()), Times.Once);
        users.Verify(x => x.CreateExternalUserAsync(It.Is<CreateExternalUserRequest>(y => y.RoleIds.SequenceEqual(new[] { AuditorRole }) && y.ExternalId == "ext-new"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task CreatingAnExternalUser_WithAConflictingPair_IsABadRequest_UnderSeparationOfDuties()
    {
        var users = ExternalUsers();

        var act = () => CreateExternal(Admin(), users, true, AuditorRole, SecurityAdminRole);
        var withoutSeparation = () => CreateExternal(Admin(), users, false, AuditorRole, SecurityAdminRole);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Separation of duties*");
        users.Verify(x => x.CreateExternalUserAsync(It.IsAny<CreateExternalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        await withoutSeparation.Should().NotThrowAsync();
    }

    [Test]
    public async Task CreatingAUser_ByAUserAdmin_WithABuiltInRole_IsLoggedByTheNewUsersId()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _users.Add(new BeaconUser { Id = 42, ExternalId = "ext-new", UserName = "newbie", IsEnabled = true }))
            .ReturnsAsync(new BaseResponse { Success = true });

        await CreateInternal(UserAdmin(), users, EditorRole);

        _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("User roles changed", StringComparison.Ordinal))
            .Which.Message.Should().Contain("user 42").And.Contain($"added roles {EditorRole}").And.NotContain("newbie");
    }

    [Test]
    public async Task EveryFunctionalRole_IsProtected_WhateverItIsNamedNow()
    {
        // A level-0 system role seeded under a name the mapping no longer uses.
        const int formerRole = 20;
        _roles.Add(Role(formerRole, "Change Board", RoleService.RoleLevels.Functional));
        _userRoles.Add(Assigned(AuditorUserId, formerRole));

        var assign = () => SetRoles(UserAdmin(), BenId, ViewerRole, formerRole);
        var changeHolder = () => Toggle(UserAdmin(), AuditorUserId);

        await assign.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.ProtectedRoleMessage);
        await changeHolder.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.ProtectedUserMessage);
    }

    [Test]
    public async Task UnderSeparationOfDuties_ARoleThatOnlyHasAFunctionalRolesName_CountsForNoPermission()
    {
        // An Admin assigns UserAdmin next to a custom role named like the Auditor role: the custom role grants nothing.
        _roles.Add(new BeaconRole { Id = 21, Name = "auditor", Level = RoleService.RoleLevels.Viewer, IsSystemRole = false });

        await SetRoles(Admin(), BenId, [ViewerRole, UserAdminRole, 21], separationOfDuties: true);

        _saved.OfType<BeaconUserRole>().Select(x => x.RoleId).Should().BeEquivalentTo([UserAdminRole, 21]);
    }

    private Task SetRoles(IBeaconPermissionAccessor permissions, int userId, params int[] roleIds)
    {
        return SetRoles(permissions, userId, roleIds, separationOfDuties: false);
    }

    private Task SetRoles(IBeaconPermissionAccessor permissions, int userId, int[] roleIds, bool separationOfDuties)
    {
        var handler = new SetUserRolesHandler(Factory(), Rules(permissions, separationOfDuties), TestProjectAccess.Actors(CallerId, "ext-ana"), new FakeTimeProvider(DateTimeOffset.UtcNow));

        return handler.Handle(new SetUserRolesCommand(userId, roleIds), CancellationToken.None);
    }

    private Task Update(IBeaconPermissionAccessor permissions, int userId, Mock<IUserManagementService>? users = null)
    {
        var handler = new UpdateUserHandler((users ?? new Mock<IUserManagementService>()).Object, Rules(permissions, false));

        return handler.Handle(new UpdateUserCommand(userId, "name", null, null, true), CancellationToken.None);
    }

    private Task Toggle(IBeaconPermissionAccessor permissions, int userId, Mock<IUserManagementService>? users = null)
    {
        var handler = new ToggleUserEnabledHandler((users ?? new Mock<IUserManagementService>()).Object, Rules(permissions, false));

        return handler.Handle(new ToggleUserEnabledCommand(userId), CancellationToken.None);
    }

    private Task CreateInternal(IBeaconPermissionAccessor permissions, Mock<IUserManagementService> users, params int[] roleIds)
    {
        var handler = new CreateInternalUserHandler(users.Object, Rules(permissions, false));

        return handler.Handle(new CreateInternalUserCommand("newbie", null, null, "a-long-password", roleIds), CancellationToken.None);
    }

    private Task CreateExternal(IBeaconPermissionAccessor permissions, Mock<IUserManagementService> users, bool separationOfDuties, params int[] roleIds)
    {
        var handler = new CreateExternalUserHandler(users.Object, Rules(permissions, separationOfDuties));

        return handler.Handle(new CreateExternalUserCommand("ext-new", "newbie", null, null, roleIds), CancellationToken.None);
    }

    private static Mock<IUserManagementService> ExternalUsers()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.CreateExternalUserAsync(It.IsAny<CreateExternalUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BaseResponse { Success = true });

        return users;
    }

    private UserAdministrationRules Rules(IBeaconPermissionAccessor permissions, bool separationOfDuties)
    {
        var mapping = new RoleMappingOptions { SeparationOfDuties = new SeparationOfDutiesOptions { Enabled = separationOfDuties } };

        return new UserAdministrationRules(
            Factory(),
            TestProjectAccess.Actors(CallerId, "ext-ana"),
            permissions,
            Options.Create(mapping),
            _logs.For<UserAdministrationRules>());
    }

    private static IBeaconPermissionAccessor UserAdmin() => TestPermissions.Holding(BeaconPermissions.UserAdmin);

    private static IBeaconPermissionAccessor Admin() => TestPermissions.Admin;

    private IDbContextFactory<BeaconContext> Factory()
    {
        foreach (var user in _users)
        {
            user.UserRoles = _userRoles
                .Where(x => x.UserId == user.Id)
                .ToList();
        }

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(BeaconRole)] = RecordingBeaconContext.MemorySet(_roles, _saved),
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, _saved),
                    [typeof(BeaconUserRole)] = RecordingBeaconContext.MemorySet(_userRoles, _saved)
                },
                _saved));

        return factory.Object;
    }

    private static BeaconRole Role(int id, string name, int level)
    {
        return new BeaconRole { Id = id, Name = name, Level = level, IsSystemRole = true };
    }

    private static BeaconUser User(int id, string externalId, bool isSuperAdmin = false)
    {
        return new BeaconUser { Id = id, ExternalId = externalId, UserName = externalId, IsEnabled = true, IsSuperAdmin = isSuperAdmin };
    }

    private BeaconUserRole Assigned(int userId, int roleId)
    {
        return new BeaconUserRole { UserId = userId, RoleId = roleId, Role = _roles.First(x => x.Id == roleId) };
    }
}
