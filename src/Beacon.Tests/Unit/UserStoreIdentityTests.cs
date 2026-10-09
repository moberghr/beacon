using System.Data;
using Beacon.Core;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The user store's identity queries, run as real service code. SQL is captured on the Npgsql provider without a
/// database (<see cref="SqlCapture"/>): the soft-delete filter is bypassed exactly where archived users must count or be
/// refused, and first-run creation is serializable. Behaviour is checked on in-memory sets
/// (<see cref="RecordingBeaconContext"/>): provisioning waits for first-run setup, refuses archived and disabled users,
/// and creates role-less users when no default role is configured.
/// </summary>
[TestFixture]
public class UserStoreIdentityTests
{
    private const string SoftDeleteFilter = "archived_time IS NULL";
    private const string Provider = "https://login.example.test/";

    [Test]
    public async Task IsFirstRun_AsksWhetherASuperAdminEverExisted_ArchivedOnesIncluded()
    {
        var capture = new SqlCapture().ThenScalar(true);

        var isFirstRun = await Service(capture).IsFirstRunAsync();

        isFirstRun.Should().BeFalse();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("is_super_admin");
        sql.Should().NotContain(SoftDeleteFilter, "an archived super admin still closes first-run setup");
    }

    [Test]
    public async Task CreateSuperAdmin_ChecksAndInserts_InOneSerializableTransaction()
    {
        var capture = new SqlCapture();

        var act = () => Service(capture).CreateSuperAdminAsync(new CreateSuperAdminRequest
        {
            UserName = "admin",
            Password = "Aa1!aaaaaaaa",
            ConfirmPassword = "Aa1!aaaaaaaa"
        });

        await act.Should().ThrowAsync<SqlCapturedException>();
        capture.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        var check = capture.Commands.Should().ContainSingle().Subject;
        check.Should().Contain("is_super_admin");
        check.Should().NotContain(SoftDeleteFilter);
    }

    [Test]
    public async Task ExternalSignIn_LooksUpArchivedUsersToo_AndChecksSetupWithoutTheSoftDeleteFilter()
    {
        var capture = new SqlCapture()
            .ThenNoRows()
            .ThenScalar(false);

        var act = () => Service(capture).GetOrCreateExternalUserAsync("sub-1", Provider, "ana", null, null, null);

        await act.Should().ThrowAsync<BeaconException>().WithMessage("First-run setup has not been completed*");
        capture.Commands.Should().HaveCount(2);
        capture.Commands[0].Should().Contain("external_id").And.Contain("identity_provider");
        capture.Commands[0].Should().NotContain(SoftDeleteFilter, "an archived subject is refused, not provisioned again");
        capture.Commands[1].Should().Contain("is_super_admin").And.NotContain(SoftDeleteFilter);
    }

    [Test]
    public async Task BearerCandidates_AreOneQuery_WithoutInternalUsersOrSuperAdmins_ArchivedIncluded()
    {
        var capture = new SqlCapture().ThenNoRows();

        var candidates = await Service(capture).GetBearerUserCandidatesAsync("sub-1", Provider, includeWithoutIdentityProvider: true);

        candidates.Should().BeEmpty();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("is_internal_user").And.Contain("is_super_admin");
        sql.Should().Contain("identity_provider IS NULL");
        sql.Should().NotContain(SoftDeleteFilter);
    }

    [Test]
    public async Task InternalLogin_PrefersAnExactUserName_InTheQueryItself()
    {
        var capture = new SqlCapture().ThenNoRows();

        var result = await Service(capture).AuthenticateInternalUserAsync("ana", "pw");

        result.Success.Should().BeFalse();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("is_internal_user");
        sql.Should().MatchRegex(@"ORDER BY CASE\s+WHEN \w+\.user_name = @\w+");
    }

    [Test]
    public async Task IsFirstRun_TrueUntilASuperAdminExists_FalseEvenWhenItIsArchived()
    {
        (await Service([SsoUser("sub-1")]).IsFirstRunAsync()).Should().BeTrue("other users never close first-run setup");

        var archivedSuperAdmin = SuperAdmin();
        archivedSuperAdmin.ArchivedTime = DateTime.UtcNow;

        (await Service([archivedSuperAdmin]).IsFirstRunAsync()).Should().BeFalse();
    }

    [Test]
    public async Task Provisioning_BeforeFirstRunSetup_IsRefused_AndCreatesNothing()
    {
        var saved = new List<object>();
        var roles = new Mock<IRoleService>();

        var act = () => Service([], saved, roleService: roles).GetOrCreateExternalUserAsync("sub-1", Provider, "ana", null, null, "Viewer");

        await act.Should().ThrowAsync<BeaconException>().WithMessage("First-run setup has not been completed*");
        saved.Should().BeEmpty();
        roles.Verify(x => x.SeedSystemRolesAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [TestCase(true, false, "*archived*")]
    [TestCase(false, false, "*disabled*")]
    public async Task ExistingArchivedOrDisabledUser_IsRefused_AndNothingIsCreated(bool archived, bool enabled, string message)
    {
        var user = SsoUser("sub-1");
        user.IsEnabled = enabled;
        user.ArchivedTime = archived ? DateTime.UtcNow : null;
        var saved = new List<object>();

        var act = () => Service([SuperAdmin(), user], saved).GetOrCreateExternalUserAsync("sub-1", Provider, "ana", null, null, "Viewer");

        await act.Should().ThrowAsync<BeaconException>().WithMessage(message);
        saved.Should().BeEmpty();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public async Task NoDefaultRole_ProvisionsAUserWithoutRoles_AndLooksNoRoleUp(string? defaultRole)
    {
        var saved = new List<object>();

        // No role set is served: a role lookup would reach the (absent) database and fail the test.
        var user = await Service([SuperAdmin()], saved).GetOrCreateExternalUserAsync("sub-1", Provider, "ana", null, null, defaultRole);

        user.Roles.Should().BeEmpty();
        saved.OfType<BeaconUser>().Should().ContainSingle()
            .Which.UserRoles.Should().BeEmpty();
    }

    [Test]
    public async Task UnknownDefaultRole_IsRefused()
    {
        var saved = new List<object>();

        var act = () => Service([SuperAdmin()], saved, roles: []).GetOrCreateExternalUserAsync("sub-1", Provider, "ana", null, null, "Nope");

        await act.Should().ThrowAsync<BeaconException>().WithMessage("*'Nope' does not exist*");
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task BearerCandidates_ExcludeInternalUsersAndSuperAdmins_AndFlagArchivedOnes()
    {
        var internalUser = SsoUser("sub-1", id: 1, provider: null);
        internalUser.IsInternalUser = true;
        var superAdmin = SsoUser("sub-1", id: 2);
        superAdmin.IsSuperAdmin = true;
        var archived = SsoUser("sub-1", id: 3);
        archived.ArchivedTime = DateTime.UtcNow;
        var preRegistered = SsoUser("sub-1", id: 4, provider: null);
        var otherProvider = SsoUser("sub-1", id: 5, provider: "https://elsewhere.example.test/");

        var store = Service([internalUser, superAdmin, archived, preRegistered, otherProvider]);

        var withPreRegistered = await store.GetBearerUserCandidatesAsync("sub-1", Provider, includeWithoutIdentityProvider: true);
        var issuerOnly = await store.GetBearerUserCandidatesAsync("sub-1", Provider, includeWithoutIdentityProvider: false);

        withPreRegistered.Select(x => (x.User.Id, x.IsArchived)).Should().Equal((3, true), (4, false));
        issuerOnly.Select(x => x.User.Id).Should().Equal(3);
    }

    [Test]
    public async Task UpdateLastLogin_TouchesOnlyTheUserWithThatId()
    {
        var bound = SsoUser("shared-id", id: 7);
        var other = SsoUser("shared-id", id: 8, provider: null);

        await Service([other, bound]).UpdateLastLoginAsync(7);

        bound.LastLoginAt.Should().NotBeNull();
        other.LastLoginAt.Should().BeNull();
    }

    private static BeaconUser SuperAdmin()
    {
        return new BeaconUser
        {
            Id = 100,
            ExternalId = Guid.NewGuid().ToString(),
            UserName = "admin",
            IsInternalUser = true,
            IsSuperAdmin = true,
            IsEnabled = true
        };
    }

    private static BeaconUser SsoUser(string externalId, int id = 1, string? provider = Provider)
    {
        return new BeaconUser
        {
            Id = id,
            ExternalId = externalId,
            IdentityProvider = provider,
            UserName = $"user-{id}",
            IsEnabled = true
        };
    }

    private static UserManagementService Service(SqlCapture capture)
    {
        return new UserManagementService(
            capture.Factory(),
            Mock.Of<IPasswordHasher>(),
            Mock.Of<IRoleService>(),
            new BeaconConfiguration());
    }

    private static UserManagementService Service(
        List<BeaconUser> users,
        List<object>? saved = null,
        List<BeaconRole>? roles = null,
        Mock<IRoleService>? roleService = null)
    {
        saved ??= [];
        var sets = new Dictionary<Type, object>
        {
            [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved)
        };
        if (roles != null)
        {
            sets[typeof(BeaconRole)] = RecordingBeaconContext.MemorySet(roles, saved);
        }

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(sets, saved));

        return new UserManagementService(
            factory.Object,
            Mock.Of<IPasswordHasher>(),
            (roleService ?? new Mock<IRoleService>()).Object,
            new BeaconConfiguration());
    }
}
