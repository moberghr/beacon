using System.Reflection;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// A UserAdmin manages users without being able to raise anyone, itself included: it never assigns or removes the Admin
/// role or a functional role, never changes its own account or roles, and never changes an Admin's, a super admin's or a
/// functional role holder's account (403). Nor does it control how anyone signs in: it creates no internal (password)
/// user and changes no user's user name or e-mail address (403), so it cannot make an account it can sign in to. An Admin
/// assigns any role and does all of these. Separation of duties, when on, refuses roles that would give one user both
/// permissions of a conflicting pair (400), whoever assigns them. Every user created and every role change is logged with
/// ids only. A role change, and "sign out everywhere" (by the same rules as any other change to the user), give the user a
/// new security stamp, which ends every session they hold.
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

    /// <summary>
    /// The requests that set a password or what a user signs in as (an external id, an identity provider, the user type),
    /// and why each may. Adding one means deciding who may send it, and a line here.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SignInIdentityRequests = new Dictionary<string, string>
    {
        [nameof(CreateInternalUserCommand)] = "Admin only: the handler refuses a UserAdmin",
        [nameof(CreateExternalUserCommand)] = "Admin or UserAdmin: the identity provider controls how the subject signs in",
        ["ChangeOwnPasswordCommand"] = "the caller's own password, against the current one",
        ["CreateSuperAdminCommand"] = "first-run setup only"
    };

    private List<BeaconRole> _roles = null!;
    private List<BeaconUser> _users = null!;
    private List<BeaconUserRole> _userRoles = null!;
    private List<object> _saved = null!;
    private LogRecorder _logs = null!;
    private RecordingSecurityAudit _audit = null!;

    [SetUp]
    public void SetUp()
    {
        _audit = new RecordingSecurityAudit(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = CallerId, IsActiveUser = true });
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

    [Test]
    public async Task ARoleChange_SignsOutEverySessionOfTheUser_AndAnUnchangedSetDoesNot()
    {
        var ben = _users.First(x => x.Id == BenId);
        ben.SecurityStamp = "before";

        await SetRoles(UserAdmin(), BenId, ViewerRole);
        var unchanged = ben.SecurityStamp;
        await SetRoles(UserAdmin(), BenId, EditorRole);

        unchanged.Should().Be("before", "the user holds the same roles");
        ben.SecurityStamp.Should().NotBeNullOrEmpty().And.NotBe("before");
    }

    [Test]
    public async Task EachAddedRole_RecordsWhoAssignedItAndWhen()
    {
        var at = new DateTimeOffset(2026, 10, 12, 9, 30, 0, TimeSpan.Zero);
        var handler = new SetUserRolesHandler(Factory(), Rules(Admin(), false), TestProjectAccess.Actors(CallerId, "ext-ana"), new FakeTimeProvider(at), _audit);

        await handler.Handle(new SetUserRolesCommand(BenId, [ViewerRole, EditorRole, ApproverRole]), CancellationToken.None);

        _saved.OfType<BeaconUserRole>().Should().HaveCount(2)
            .And.OnlyContain(x => x.UserId == BenId && x.AssignedByUserId == "ext-ana" && x.AssignedAt == at.UtcDateTime);
        _audit.Single("ext-ana").Should().BeEquivalentTo(
            new
            {
                Action = SecurityAuditActions.RolesChanged,
                ActorUserId = (int?)CallerId,
                ResourceId = BenId.ToString(),
                DetailsJson = $"{{\"addedRoleIds\":[{EditorRole},{ApproverRole}],\"removedRoleIds\":[]}}"
            },
            x => x.ExcludingMissingMembers());
        _audit.Ways.Should().Equal("staged");
    }

    [Test]
    public async Task AUserAdmin_SignsAnotherUserOutEverywhere_AndItIsLoggedWithIdsOnly()
    {
        var ben = _users.First(x => x.Id == BenId);

        await SignOutEverywhere(UserAdmin(), BenId);

        ben.SecurityStamp.Should().NotBeNullOrEmpty();
        var entry = _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Every session of user", StringComparison.Ordinal)).Subject;
        entry.Message.Should().Be($"Every session of user {BenId} signed out, by user {CallerId}");
        _users.Where(x => x.Id != BenId).Should().OnlyContain(x => x.SecurityStamp == null, "only that user's sessions end");
        _audit.Single("ext-ana").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.SignedOutEverywhere, ActorUserId = (int?)CallerId, ResourceId = BenId.ToString() },
            x => x.ExcludingMissingMembers());
    }

    [TestCase(AdminUserId)]
    [TestCase(SuperAdminId)]
    [TestCase(AuditorUserId)]
    public async Task AUserAdmin_CannotSignOutAnAdminASuperAdminOrAFunctionalRoleHolder_ButAnAdminCan(int userId)
    {
        var act = () => SignOutEverywhere(UserAdmin(), userId);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.ProtectedUserMessage);
        _users.First(x => x.Id == userId).SecurityStamp.Should().BeNull();

        await SignOutEverywhere(Admin(), userId);

        _users.First(x => x.Id == userId).SecurityStamp.Should().NotBeNullOrEmpty();
    }

    [Test]
    public async Task SigningOutEverywhere_IsRefusedToAUserAdminForThemself_AndToACallerWithoutThePermission()
    {
        var own = () => SignOutEverywhere(UserAdmin(), CallerId);
        var none = () => SignOutEverywhere(TestPermissions.None, BenId);
        var unknown = () => SignOutEverywhere(Admin(), 404);

        await own.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.OwnAccountMessage);
        await none.Should().ThrowAsync<BeaconForbiddenException>();
        await unknown.Should().ThrowAsync<BeaconNotFoundException>();
        _users.Should().OnlyContain(x => x.SecurityStamp == null);
    }

    [Test]
    public async Task ACaller_SignsOutTheirOwnSessionsEverywhere_WithoutAnyPermission_LoggedInTheSamePlace()
    {
        var caller = _users.First(x => x.Id == CallerId);

        await SignOutEverywhere(TestPermissions.None, null);

        caller.SecurityStamp.Should().NotBeNullOrEmpty();
        _users.Where(x => x.Id != CallerId).Should().OnlyContain(x => x.SecurityStamp == null);
        _logs.Entries.Should().ContainSingle(x => x.Message == $"Every session of user {CallerId} signed out, by user {CallerId}");
    }

    [Test]
    public async Task SigningOutEverywhere_WithoutUserManagement_ChangesNothing_AndSaysSo()
    {
        var other = () => SignOutEverywhere(Admin(), BenId, userManagement: false);
        var own = () => SignOutEverywhere(Admin(), null, userManagement: false);

        await other.Should().ThrowAsync<BeaconConflictException>().WithMessage("*user management is off*");
        await own.Should().ThrowAsync<BeaconConflictException>();
        _users.Should().OnlyContain(x => x.SecurityStamp == null, "a stamp no session is checked against would end nothing");
        _logs.Entries.Should().NotContain(x => x.Message.StartsWith("Every session of user", StringComparison.Ordinal));
    }

    [Test]
    public async Task SigningOutOnesOwnSessionsEverywhere_IsRefusedToAnApiKeyAnMcpCallerAndNobody()
    {
        var apiKey = ActorOfKind(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.ApiKey, BeaconUserId = CallerId, IsApiKey = true });
        var mcpCaller = ActorOfKind(new BeaconActor("ext-ana", false) { Kind = BeaconPrincipalKind.McpCaller, BeaconUserId = CallerId });

        var byKey = () => SignOutEverywhere(TestPermissions.None, null, actors: apiKey);
        var byMcp = () => SignOutEverywhere(TestPermissions.None, null, actors: mcpCaller);
        var byNobody = () => SignOutEverywhere(TestPermissions.None, null, actors: ActorOfKind(BeaconActor.Nobody));

        await byKey.Should().ThrowAsync<BeaconConflictException>();
        await byMcp.Should().ThrowAsync<BeaconConflictException>();
        await byNobody.Should().ThrowAsync<BeaconConflictException>();
        _users.Should().OnlyContain(x => x.SecurityStamp == null);
    }

    [Test]
    public async Task AGrantByACallerWithoutAUserId_IsLoggedExplicitly()
    {
        var withoutUserId = ActorOfKind(new BeaconActor(null, true) { Kind = BeaconPrincipalKind.Session });
        var handler = new SetUserRolesHandler(
            Factory(),
            Rules(Admin(), false, withoutUserId),
            withoutUserId,
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            _audit);

        await handler.Handle(new SetUserRolesCommand(BenId, [ViewerRole, EditorRole]), CancellationToken.None);

        _saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.AssignedByUserId.Should().BeNull();
        _logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("by a caller without a user id", StringComparison.Ordinal));
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
        users.Verify(x => x.CreateExternalUserAsync(It.Is<CreateExternalUserRequest>(y => y.AssignedByUserId == "ext-ana"), It.IsAny<CancellationToken>()), Times.Exactly(2), "the creator is recorded as who assigned the roles");
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
            .Setup(x => x.CreateExternalUserAsync(It.IsAny<CreateExternalUserRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _users.Add(new BeaconUser { Id = 42, ExternalId = "ext-new", UserName = "newbie", IsEnabled = true }))
            .ReturnsAsync(new BaseResponse { Success = true });

        await CreateExternal(UserAdmin(), users, false, EditorRole);

        _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("User created", StringComparison.Ordinal))
            .Which.Message.Should().Be($"User created: user 42, internal False, by user {CallerId}");
        _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("User roles changed", StringComparison.Ordinal))
            .Which.Message.Should().Contain("user 42").And.Contain($"added roles {EditorRole}").And.NotContain("newbie");
    }

    [TestCase(null)]
    [TestCase(EditorRole)]
    [TestCase(ViewerRole)]
    public async Task AUserAdmin_CreatingAnInternalUser_IsForbidden_AndCreatesNobody(int? roleId)
    {
        // The password would be the UserAdmin's: it could sign in as the new user and use whatever access it gave it.
        var users = new Mock<IUserManagementService>();
        users.Setup(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new BaseResponse { Success = true });
        var roleIds = roleId == null ? Array.Empty<int>() : new[] { roleId.Value };

        var act = () => CreateInternal(UserAdmin(), users, roleIds);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.PasswordUserMessage);
        users.Verify(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>()), Times.Never, "a refusal creates nobody");
        _saved.Should().BeEmpty();
        _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("Access refused: user ", StringComparison.Ordinal))
            .Which.Level.Should().Be(LogLevel.Warning);
        _logs.Entries.Should().NotContain(x => x.Message.StartsWith("User created", StringComparison.Ordinal));
    }

    [Test]
    public async Task AnAdmin_CreatesAnInternalUser_AndTheNewUserIsLogged()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.CreateInternalUserAsync(It.IsAny<CreateInternalUserRequest>(), It.IsAny<CancellationToken>()))
            .Callback(() => _users.Add(new BeaconUser { Id = 42, ExternalId = "int-new", UserName = "newbie", IsInternalUser = true, IsEnabled = true }))
            .ReturnsAsync(new BaseResponse { Success = true });

        await CreateInternal(Admin(), users, EditorRole);

        users.Verify(x => x.CreateInternalUserAsync(It.Is<CreateInternalUserRequest>(y => y.UserName == "newbie" && y.RoleIds.SequenceEqual(new[] { EditorRole }) && y.AssignedByUserId == "ext-ana"), It.IsAny<CancellationToken>()), Times.Once);
        _logs.Entries.Should().ContainSingle(x => x.Message.StartsWith("User created", StringComparison.Ordinal))
            .Which.Message.Should().Be($"User created: user 42, internal True, by user {CallerId}");
    }

    [TestCase(null, "ben-renamed", null)]
    [TestCase(null, "ext-ben", "mine@example.test")]
    [TestCase("ben@example.test", "ext-ben", "mine@example.test")]
    [TestCase("ben@example.test", "ext-ben", null)]
    [TestCase("ben@example.test", "EXT-BEN", "ben@example.test")]
    public async Task AUserAdmin_ChangingWhatAnotherUserSignsInWith_IsForbidden(string? storedEmail, string userName, string? email)
    {
        _users.First(x => x.Id == BenId).Email = storedEmail;
        var users = UpdatableUsers();

        var act = () => Update(UserAdmin(), BenId, userName, email, users);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(UserAdministrationRules.SignInIdentityMessage);
        users.Verify(x => x.UpdateUserAsync(It.IsAny<UpdateUserRequest>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(null, null)]
    [TestCase(null, "")]
    [TestCase("ben@example.test", "ben@example.test")]
    public async Task AUserAdmin_KeepingWhatTheUserSignsInWith_ChangesTheRestOfTheAccount(string? storedEmail, string? email)
    {
        _users.First(x => x.Id == BenId).Email = storedEmail;
        var users = UpdatableUsers();

        await Update(UserAdmin(), BenId, "ext-ben", email, users);

        users.Verify(x => x.UpdateUserAsync(It.Is<UpdateUserRequest>(y => y.UserId == BenId && y.UserName == "ext-ben" && y.DisplayName == "Display"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AnAdmin_ChangesWhatAUserSignsInWith()
    {
        _users.First(x => x.Id == BenId).Email = "ben@example.test";
        var users = UpdatableUsers();

        await Update(Admin(), BenId, "ben-renamed", "new@example.test", users);

        users.Verify(x => x.UpdateUserAsync(It.Is<UpdateUserRequest>(y => y.UserId == BenId && y.UserName == "ben-renamed" && y.Email == "new@example.test"), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void OnlyTheReviewedRequests_SetAPasswordOrWhatAUserSignsInAs()
    {
        // No request resets another user's password or changes an external id, an identity provider or the user type.
        var requests = BeaconApiRoutes.RequestTypes()
            .Where(x => CarriesSignInIdentity(x, 0))
            .Select(x => x.Name)
            .ToList();

        requests.Should().BeEquivalentTo(SignInIdentityRequests.Keys);
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
        var handler = new SetUserRolesHandler(Factory(), Rules(permissions, separationOfDuties), TestProjectAccess.Actors(CallerId, "ext-ana"), new FakeTimeProvider(DateTimeOffset.UtcNow), _audit);

        return handler.Handle(new SetUserRolesCommand(userId, roleIds), CancellationToken.None);
    }

    private Task SignOutEverywhere(IBeaconPermissionAccessor permissions, int? userId, bool userManagement = true, IBeaconActorAccessor? actors = null)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;
        var handler = new SignUserOutEverywhereHandler(
            Rules(permissions, false),
            new UserSessionStore(Factory(), _audit),
            actors ?? TestProjectAccess.Actors(CallerId, "ext-ana"),
            configuration,
            _logs.For<SignUserOutEverywhereHandler>());

        return handler.Handle(new SignUserOutEverywhereCommand(userId), CancellationToken.None);
    }

    private static IBeaconActorAccessor ActorOfKind(BeaconActor actor)
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(actor);

        return accessor.Object;
    }

    // Keeps what the user signs in with, as stored.
    private Task Update(IBeaconPermissionAccessor permissions, int userId, Mock<IUserManagementService>? users = null)
    {
        var stored = _users.FirstOrDefault(x => x.Id == userId);

        return Update(permissions, userId, stored?.UserName ?? "name", stored?.Email, users);
    }

    private Task Update(IBeaconPermissionAccessor permissions, int userId, string userName, string? email, Mock<IUserManagementService>? users = null)
    {
        var handler = new UpdateUserHandler((users ?? new Mock<IUserManagementService>()).Object, Rules(permissions, false));

        return handler.Handle(new UpdateUserCommand(userId, userName, email, "Display", true), CancellationToken.None);
    }

    private static Mock<IUserManagementService> UpdatableUsers()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.UpdateUserAsync(It.IsAny<UpdateUserRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BaseResponse { Success = true });

        return users;
    }

    // A password, an external id, an identity provider or the user type, on the request or on a Beacon type it carries.
    private static bool CarriesSignInIdentity(Type type, int depth)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.Name.Contains("Password", StringComparison.OrdinalIgnoreCase)
                || property.Name is "ExternalId" or "IdentityProvider" or "IsInternalUser")
            {
                return true;
            }

            var carried = property.PropertyType;
            if (depth < 2 && carried.IsClass && carried.Namespace?.StartsWith("Beacon.", StringComparison.Ordinal) == true
                && CarriesSignInIdentity(carried, depth + 1))
            {
                return true;
            }
        }

        return false;
    }

    private Task Toggle(IBeaconPermissionAccessor permissions, int userId, Mock<IUserManagementService>? users = null)
    {
        var handler = new ToggleUserEnabledHandler((users ?? new Mock<IUserManagementService>()).Object, Rules(permissions, false));

        return handler.Handle(new ToggleUserEnabledCommand(userId), CancellationToken.None);
    }

    private Task CreateInternal(IBeaconPermissionAccessor permissions, Mock<IUserManagementService> users, params int[] roleIds)
    {
        var handler = new CreateInternalUserHandler(users.Object, Rules(permissions, false), TestProjectAccess.Actors(CallerId, "ext-ana"));

        return handler.Handle(new CreateInternalUserCommand("newbie", null, null, "a-long-password", roleIds), CancellationToken.None);
    }

    private Task CreateExternal(IBeaconPermissionAccessor permissions, Mock<IUserManagementService> users, bool separationOfDuties, params int[] roleIds)
    {
        var handler = new CreateExternalUserHandler(users.Object, Rules(permissions, separationOfDuties), TestProjectAccess.Actors(CallerId, "ext-ana"));

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

    private UserAdministrationRules Rules(IBeaconPermissionAccessor permissions, bool separationOfDuties, IBeaconActorAccessor? actors = null)
    {
        var mapping = new RoleMappingOptions { SeparationOfDuties = new SeparationOfDutiesOptions { Enabled = separationOfDuties } };

        return new UserAdministrationRules(
            Factory(),
            actors ?? TestProjectAccess.Actors(CallerId, "ext-ana"),
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
