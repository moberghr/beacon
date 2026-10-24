using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.ChangeGovernance;
using FluentAssertions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// A reviewer read again inside a decision's transaction: the stored user's row is taken first, then what it holds now is
/// read, as the permission accessor derives it (an Admin holds every permission, a functional system role its own, any
/// other role nothing). A user that is gone, disabled, archived or signed in with a security stamp it no longer has holds
/// nothing. A reviewer without a stored user is not read again. The SQL is checked on PostgreSQL and SQL Server.
/// </summary>
[TestFixture]
public class ReviewerAuthorityTests
{
    private const string Ada = "ext-ada";

    [Test]
    public async Task AnActiveApprover_HoldsTheApproverPermission()
    {
        var store = new ChangeApprovalStore();
        store.UserHolding(7, Ada, BeaconPermissions.Approver);

        var live = await ReadAsync(store, Reviewer(7));

        live.Should().NotBeNull();
        live!.IsActive.Should().BeTrue();
        live.Permissions.IsAdmin.Should().BeFalse();
        live.Permissions.Permissions.Should().BeEquivalentTo([BeaconPermissions.Approver]);
    }

    [Test]
    public async Task AnAdmin_OrASuperAdmin_HoldsEveryPermission()
    {
        var store = new ChangeApprovalStore();
        store.UserHolding(7, Ada, RoleService.RoleNames.Admin);
        store.User(8, "ext-ben").IsSuperAdmin = true;

        var admin = await ReadAsync(store, Reviewer(7));
        var superAdmin = await ReadAsync(store, Reviewer(8));

        admin!.Permissions.IsAdmin.Should().BeTrue();
        superAdmin!.Permissions.IsAdmin.Should().BeTrue();
    }

    // Only a system role at the functional level grants its permission, as the permission accessor reads it.
    [Test]
    public async Task ARoleThatIsNoFunctionalSystemRole_GrantsNothing()
    {
        var store = new ChangeApprovalStore();
        var user = store.UserHolding(7, Ada, BeaconPermissions.Approver);
        user.UserRoles[0].Role.IsSystemRole = false;

        var live = await ReadAsync(store, Reviewer(7));

        live!.IsActive.Should().BeTrue();
        live.Permissions.Holds(BeaconPermissions.Approver).Should().BeFalse();
    }

    [TestCase("disabled")]
    [TestCase("archived")]
    [TestCase("gone")]
    public async Task AUserThatIsNoLongerActive_HoldsNothing(string state)
    {
        var store = new ChangeApprovalStore();
        var user = store.UserHolding(7, Ada, RoleService.RoleNames.Admin);
        switch (state)
        {
            case "disabled":
                user.IsEnabled = false;
                break;
            case "archived":
                user.ArchivedTime = DateTime.UtcNow;
                break;
            default:
                store.Users.Clear();
                break;
        }

        var live = await ReadAsync(store, Reviewer(7));

        live.Should().Be(LiveReviewer.Inactive);
    }

    // A session signed in with a stamp the user no longer has (a role change, a password reset, every session signed out)
    // holds nothing; one carrying the current stamp holds its roles.
    [TestCase("old-stamp", false)]
    [TestCase("current-stamp", true)]
    public async Task ASessionWithAStampTheUserNoLongerHas_HoldsNothing(string sessionStamp, bool active)
    {
        var store = new ChangeApprovalStore();
        store.UserHolding(7, Ada, RoleService.RoleNames.Admin).SecurityStamp = "current-stamp";
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(BeaconClaims.SessionStamp, sessionStamp, ClaimValueTypes.String, BeaconClaims.Issuer)], "Cookies"));

        var live = await ReadAsync(store, Reviewer(7), Mock.Of<IBeaconRequestPrincipal>(x => x.User == principal));

        live!.IsActive.Should().Be(active);
        live.Permissions.IsAdmin.Should().Be(active);
    }

    // Nothing stored to read again: a session without a stored user (its claims were checked before), an API key or an MCP
    // caller (which never hold a functional permission).
    [TestCase("session")]
    [TestCase("api key")]
    [TestCase("mcp caller")]
    public async Task AReviewerWithoutAStoredUserSession_IsNotReadAgain(string caller)
    {
        var kind = caller switch
        {
            "session" => BeaconPrincipalKind.Session,
            "api key" => BeaconPrincipalKind.ApiKey,
            _ => BeaconPrincipalKind.McpCaller
        };
        var store = new ChangeApprovalStore();
        store.UserHolding(7, Ada, RoleService.RoleNames.Admin);
        var reviewer = new BeaconActor(Ada, false) { Kind = kind, BeaconUserId = kind == BeaconPrincipalKind.Session ? null : 7 };

        var live = await ReadAsync(store, reviewer);

        live.Should().BeNull();
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadLocked_TakesTheUsersRowFirst_ThenReadsWhatItHolds_Translates(bool sqlServer)
    {
        var capture = (sqlServer ? SqlCapture.OnSqlServer() : new SqlCapture())
            .ThenRowsAffected(1)
            .ThenNoRows();
        await using var context = await capture.Factory().CreateDbContextAsync(CancellationToken.None);

        var live = await TestChangeApprovals.Reviewers().ReadLockedAsync(context, Reviewer(7), CancellationToken.None);

        live.Should().Be(LiveReviewer.Inactive);
        capture.Commands.Should().HaveCount(2);
        if (sqlServer)
        {
            capture.Commands[0].Should().Be("UPDATE [u]\nSET [u].[SecurityStamp] = [u].[SecurityStamp]\nFROM [beacon].[Users] AS [u]\nWHERE [u].[ArchivedTime] IS NULL AND [u].[Id] = @userId");
            capture.Commands[1].Should()
                .Contain("FROM [beacon].[Users] AS [u]")
                .And.Contain("[u].[IsEnabled] = CAST(1 AS bit) AND [u].[ArchivedTime] IS NULL")
                .And.Contain("[u].[IsSuperAdmin]")
                .And.Contain("[r].[Name] = N'Admin'")
                .And.Contain("[r0].[IsSystemRole] = CAST(1 AS bit) AND [r0].[Level] = 0")
                .And.Contain("[u].[Id] = @userId");
            return;
        }

        capture.Commands[0].Should().Be("UPDATE beacon.users AS u\nSET security_stamp = u.security_stamp\nWHERE u.archived_time IS NULL AND u.id = @userId");
        capture.Commands[1].Should()
            .Contain("FROM beacon.users AS u")
            .And.Contain("u.is_enabled AND u.archived_time IS NULL")
            .And.Contain("u.is_super_admin")
            .And.Contain("r.name = 'Admin'")
            .And.Contain("r0.is_system_role AND r0.level = 0")
            .And.Contain("u.id = @userId");
    }

    private static BeaconActor Reviewer(int userId) =>
        new(Ada, false) { Kind = BeaconPrincipalKind.User, BeaconUserId = userId, IsActiveUser = true };

    private static async Task<LiveReviewer?> ReadAsync(ChangeApprovalStore store, BeaconActor reviewer, IBeaconRequestPrincipal? principal = null)
    {
        await using var context = await store.Factory().CreateDbContextAsync(CancellationToken.None);

        return await TestChangeApprovals.Reviewers(principal).ReadLockedAsync(context, reviewer, CancellationToken.None);
    }
}
