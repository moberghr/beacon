using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ChangeGovernance;

/// <summary>
/// A grant of the Admin role under the approval workflow (<see cref="ApprovalWorkflowOptions.CoversAdminRoleGrants"/>): a
/// save that gives a user the Admin role — a role change, or a user created with it — applies everything else and submits
/// the grant, by ids only, from a requester Beacon can name; removing the Admin role and granting a functional role apply
/// at once; a user provisioned at a first sign-in never gets the Admin role, and the legacy single-role assignment grants it
/// to nobody. Only a live Admin other than the requester reads and decides the grant, whatever self-approval allows; an
/// approval applies it while the user exists and does not hold the role yet, recorded as assigned by the requester, with a
/// new security stamp and the role change's event.
/// </summary>
[TestFixture]
public class AdminRoleGrantApprovalTests
{
    private const string Ada = "ext-ada";
    private const string Ben = "ext-ben";
    private const int AdaId = 1;
    private const int BenId = 2;
    private const int CidId = 3;
    private const int AdminRole = 11;
    private const int EditorRole = 12;
    private const int ViewerRole = 13;
    private const int ApproverRole = 14;

    [Test]
    public async Task AddingTheAdminRole_SubmitsTheGrant_AndAppliesTheOtherRoles()
    {
        var harness = new Harness();
        harness.Holds(CidId, ViewerRole);
        var cid = harness.Store.Users.Single(x => x.Id == CidId);

        await harness.SetRoles(Ada, AdaId).Handle(new SetUserRolesCommand(CidId, [ViewerRole, EditorRole, AdminRole]), CancellationToken.None);
        harness.Store.Commit();

        harness.Store.Saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.RoleId.Should().Be(EditorRole);
        cid.SecurityStamp.Should().NotBe("before", "the Editor role applied, which ends the user's sessions");
        var request = harness.Store.Requests.Should().ContainSingle().Subject;
        request.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.AdminRoleGrant,
                ResourceId = CidId,
                ResourceName = "User 3",
                Status = ApprovalStatus.Pending,
                RequestedByUserId = Ada,
                DefinitionHash = AdminRoleGrantApproval.Hash(CidId, AdminRole),
                ChangeSummary = AdminRoleGrantApproval.ChangeSummary,
                PreviousJson = (string?)null
            },
            x => x.ExcludingMissingMembers());
        request.ProposedJson.Should().Be("""{"userId":3,"roleId":11,"roleName":"Admin"}""");
        harness.Store.Offline.Commits.Should().Be(1);
        harness.Audit.AssertStagedEventsSaved();
        harness.Audit.Events.Select(x => x.Action).Should().BeEquivalentTo([SecurityAuditActions.RolesChanged, SecurityAuditActions.ChangeSubmitted]);
        harness.Audit.Events.Single(x => x.Action == SecurityAuditActions.RolesChanged).DetailsJson.Should().Contain($"[{EditorRole}]").And.NotContain(AdminRole.ToString());
    }

    [Test]
    public async Task AddingOnlyTheAdminRole_ChangesNoRole_AndKeepsTheUsersSessions()
    {
        var harness = new Harness();
        var cid = harness.Store.Users.Single(x => x.Id == CidId);

        await harness.SetRoles(Ada, AdaId).Handle(new SetUserRolesCommand(CidId, [AdminRole]), CancellationToken.None);
        harness.Store.Commit();

        harness.Store.Saved.OfType<BeaconUserRole>().Should().BeEmpty();
        cid.SecurityStamp.Should().Be("before");
        harness.Store.Requests.Should().ContainSingle().Which.Kind.Should().Be(ChangeApprovalKind.AdminRoleGrant);
        harness.Audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.ChangeSubmitted);
    }

    // Another Admin's pending grant of the same role: saving it again adds no second request and refuses nothing.
    [Test]
    public async Task TheSameGrantSavedAgain_AddsNoSecondRequest()
    {
        var harness = new Harness();
        var pending = harness.Store.AddRequest(ChangeApprovalKind.AdminRoleGrant, CidId, AdminRoleGrantApproval.Hash(CidId, AdminRole), Ben);

        await harness.SetRoles(Ada, AdaId).Handle(new SetUserRolesCommand(CidId, [AdminRole]), CancellationToken.None);
        harness.Store.Commit();

        harness.Store.Requests.Should().ContainSingle().Which.Should().BeSameAs(pending);
        pending.Status.Should().Be(ApprovalStatus.Pending);
    }

    [Test]
    public async Task RemovingTheAdminRole_AndGrantingAFunctionalRole_ApplyAtOnce()
    {
        var harness = new Harness();
        harness.Holds(CidId, AdminRole, ViewerRole);

        await harness.SetRoles(Ada, AdaId).Handle(new SetUserRolesCommand(CidId, [ViewerRole, ApproverRole]), CancellationToken.None);

        harness.Store.UserRoles.Should().NotContain(x => x.UserId == CidId && x.RoleId == AdminRole);
        harness.Store.Saved.OfType<BeaconUserRole>().Should().ContainSingle(x => x.RoleId == ApproverRole);
        harness.Store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
    }

    [Test]
    public async Task WithTheGrantNotReviewed_TheAdminRoleAppliesAtOnce()
    {
        var harness = new Harness(covers: false);

        await harness.SetRoles(Ada, AdaId).Handle(new SetUserRolesCommand(CidId, [AdminRole]), CancellationToken.None);

        harness.Store.Saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.RoleId.Should().Be(AdminRole);
        harness.Store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
    }

    // Self-approval allowed or not, a grant needs a requester Beacon can name: nothing is written.
    [Test]
    public async Task AGrantByACallerWithoutAUserId_IsRefused_AndWritesNothing()
    {
        var harness = new Harness(allowSelfApproval: true);

        var act = () => harness.SetRoles(null, AdaId).Handle(new SetUserRolesCommand(CidId, [EditorRole, AdminRole]), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(AdminRoleGrantApproval.UnidentifiedRequesterMessage);
        harness.Store.Saved.Should().BeEmpty();
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AUserCreatedWithTheAdminRole_IsCreatedWithTheOtherRoles_AndTheGrantWaits(bool internalUser)
    {
        var harness = new Harness();

        var response = internalUser
            ? await harness.Users(Ada).CreateInternalUserAsync(new CreateInternalUserRequest { UserName = "dora", Password = "Aa1!aaaaaaaa", RoleIds = [EditorRole, AdminRole], AssignedByUserId = Ada })
            : await harness.Users(Ada).CreateExternalUserAsync(new CreateExternalUserRequest { ExternalId = "ext-dora", UserName = "dora", RoleIds = [EditorRole, AdminRole], AssignedByUserId = Ada });
        harness.Store.Commit();

        response.Success.Should().BeTrue();
        response.Message.Should().EndWith(AdminRoleGrantApproval.GrantWaitsMessage);
        var created = harness.Store.Saved.OfType<BeaconUser>().Should().ContainSingle().Subject;
        created.UserRoles.Select(x => x.RoleId).Should().Equal(EditorRole);
        harness.Store.Requests.Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { Kind = ChangeApprovalKind.AdminRoleGrant, ResourceId = created.Id, RequestedByUserId = Ada },
            x => x.ExcludingMissingMembers());
        harness.Store.Requests.Single().ProposedJson.Should().NotContain("dora");
        harness.Store.Offline.Commits.Should().Be(1);
        harness.Audit.Events.Select(x => x.Action).Should().BeEquivalentTo([SecurityAuditActions.UserCreated, SecurityAuditActions.ChangeSubmitted]);
        harness.Audit.Events.Single(x => x.Action == SecurityAuditActions.UserCreated).DetailsJson.Should().NotContain(AdminRole.ToString());
    }

    // The legacy single-role assignment, which Beacon itself never calls, grants the Admin role to nobody while grants are
    // reviewed; any other role it still assigns.
    [Test]
    public async Task TheLegacyRoleAssignment_GrantsTheAdminRoleToNobody_WhileGrantsAreReviewed()
    {
        var harness = new Harness();
#pragma warning disable CS0618
        var admin = await harness.Users(Ada).AssignRoleAsync(CidId, AdminRole, Ada);
        var editor = await harness.Users(Ada).AssignRoleAsync(CidId, EditorRole, Ada);
#pragma warning restore CS0618

        admin.Success.Should().BeFalse();
        editor.Success.Should().BeTrue();
        harness.Store.Saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.RoleId.Should().Be(EditorRole);
        harness.Store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
    }

    // A host whose first sign-in default is the Admin role provisions the user with no role while grants are reviewed (a
    // Warning says so), and with the role otherwise.
    [TestCase(true, new int[0])]
    [TestCase(false, new[] { AdminRole })]
    public async Task AUserProvisionedAtAFirstSignIn_GetsNoAdminRole_WhileGrantsAreReviewed(bool covers, int[] expected)
    {
        var harness = new Harness(covers);
        harness.Store.Users.Single(x => x.Id == AdaId).IsSuperAdmin = true;
        var logs = new LogRecorder();

        var user = await harness.Users(null, logs.For<UserManagementService>())
            .GetOrCreateExternalUserAsync("ext-erin", "https://idp.example", "erin", null, null, "Admin");

        user.Roles.Select(x => x.Id).Should().Equal(expected);
        harness.Store.Saved.OfType<BeaconUser>().Should().ContainSingle().Which.UserRoles.Select(x => x.RoleId).Should().Equal(expected);
        harness.Store.Saved.OfType<ChangeApprovalRequest>().Should().BeEmpty();
        logs.Entries.Where(x => x.Level == LogLevel.Warning).Should().HaveCount(covers ? 1 : 0);
    }

    // Only a live Admin reads a grant: an Approver who is no Admin lists none, opens none (404) and decides none (403).
    [Test]
    public async Task AnApproverWhoIsNoLiveAdmin_NeitherReadsNorDecidesAGrant()
    {
        var harness = new Harness();
        var grant = harness.PendingGrant(Ada);
        var approver = TestPermissions.Holding(BeaconPermissions.Approver);
        var scope = ChangeApprovalScope.Approver;

        var list = await harness.Decisions(Ben, approver).GetPendingAsync(new GetChangeApprovalsQuery(), null, scope, CancellationToken.None);
        var detail = await harness.Decisions(Ben, approver).GetDetailAsync(grant.Id, scope, CancellationToken.None);
        var approve = () => harness.Decisions(Ben, approver).ApproveAsync(grant.Id, null, grant.PayloadHash, CancellationToken.None);
        var reject = () => harness.Decisions(Ben, approver).RejectAsync(grant.Id, null, CancellationToken.None);

        list.Items.Should().BeEmpty();
        detail.Should().BeNull();
        await approve.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalService.AdminGrantLiveAdminRequiredMessage);
        await reject.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ChangeApprovalService.AdminGrantLiveAdminRequiredMessage);
        grant.Status.Should().Be(ApprovalStatus.Pending);
    }

    // The requester never approves their own grant, even on a host that allows self-approval.
    [TestCase(true)]
    [TestCase(false)]
    public async Task TheRequester_NeverApprovesTheirOwnGrant(bool allowSelfApproval)
    {
        var harness = new Harness(allowSelfApproval: allowSelfApproval);
        var grant = harness.PendingGrant(Ada);

        var detail = await harness.Decisions(Ada, TestPermissions.Admin).GetDetailAsync(grant.Id, LiveAdmin, CancellationToken.None);
        var approve = () => harness.Decisions(Ada, TestPermissions.Admin).ApproveAsync(grant.Id, null, grant.PayloadHash, CancellationToken.None);

        detail!.MayApprove.Should().BeFalse();
        await approve.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(QueryApprovalService.OwnRequestMessage);
        harness.Store.Saved.OfType<BeaconUserRole>().Should().BeEmpty();
    }

    [Test]
    public async Task ALiveAdminsDetail_ShowsTheUserByTheirLabel_AndTheGrantByIds()
    {
        var harness = new Harness();
        harness.Store.Users.Single(x => x.Id == CidId).DisplayName = "Cid Reyes";
        var grant = harness.PendingGrant(Ada);

        var detail = await harness.Decisions(Ben, TestPermissions.Admin).GetDetailAsync(grant.Id, LiveAdmin, CancellationToken.None);

        detail.Should().BeEquivalentTo(
            new
            {
                Kind = ChangeApprovalKind.AdminRoleGrant,
                ResourceName = "Cid Reyes",
                ResourceExists = true,
                MayApprove = true,
                IsStale = false,
                ProposedAdminRoleGrant = new { UserId = CidId, RoleId = AdminRole, RoleName = "Admin" }
            },
            x => x.ExcludingMissingMembers());
    }

    [Test]
    public async Task AnApproval_GrantsTheRole_AsAssignedByTheRequester_EndsTheUsersSessions_AndRecordsTheChange()
    {
        var harness = new Harness();
        var cid = harness.Store.Users.Single(x => x.Id == CidId);
        var grant = harness.PendingGrant(Ada);

        var decision = await harness.Decisions(Ben, TestPermissions.Admin).ApproveAsync(grant.Id, null, grant.PayloadHash, CancellationToken.None);

        decision.Status.Should().Be(ApprovalStatus.Approved);
        grant.Status.Should().Be(ApprovalStatus.Approved);
        grant.ReviewedByUserId.Should().Be(Ben);
        harness.Store.Saved.OfType<BeaconUserRole>().Should().ContainSingle().Which.Should().BeEquivalentTo(
            new { UserId = CidId, RoleId = AdminRole, AssignedByUserId = Ada },
            x => x.ExcludingMissingMembers());
        cid.SecurityStamp.Should().NotBe("before");
        harness.Store.Offline.Commits.Should().Be(1);
        harness.Audit.AssertStagedEventsSaved();
        harness.Audit.Events.Select(x => x.Action).Should().BeEquivalentTo([SecurityAuditActions.RolesChanged, SecurityAuditActions.ChangeApproved]);
        harness.Audit.Events.Single(x => x.Action == SecurityAuditActions.RolesChanged).ResourceId.Should().Be(CidId.ToString());
    }

    // A user who meanwhile holds the Admin role, or no longer exists (archived), takes no grant: 409, nothing applied.
    [TestCase(true, AdminRoleGrantApproval.AlreadyAdminMessage)]
    [TestCase(false, ChangeApprovalService.ResourceGoneMessage)]
    public async Task AnApproval_OfAUserWhoHoldsTheRoleOrIsGone_IsRefused(bool holdsAdmin, string message)
    {
        var harness = new Harness();
        var grant = harness.PendingGrant(Ada);
        if (holdsAdmin)
        {
            harness.Holds(CidId, AdminRole);
        }
        else
        {
            harness.Store.Users.RemoveAll(x => x.Id == CidId);
        }

        var approve = () => harness.Decisions(Ben, TestPermissions.Admin).ApproveAsync(grant.Id, null, grant.PayloadHash, CancellationToken.None);

        await approve.Should().ThrowAsync<BeaconConflictException>().WithMessage(message);
        grant.Status.Should().Be(ApprovalStatus.Pending);
        harness.Store.Saved.OfType<BeaconUserRole>().Should().BeEmpty();
        harness.Store.Offline.Commits.Should().Be(0);
    }

    // A rejection by a live Admin (the requester withdrawing included) applies nothing.
    [Test]
    public async Task ARejection_GrantsNothing()
    {
        var harness = new Harness();
        var grant = harness.PendingGrant(Ada);

        await harness.Decisions(Ada, TestPermissions.Admin).RejectAsync(grant.Id, "withdrawn", CancellationToken.None);

        grant.Status.Should().Be(ApprovalStatus.Rejected);
        harness.Store.Saved.OfType<BeaconUserRole>().Should().BeEmpty();
        harness.Audit.Events.Select(x => x.Action).Should().Equal(SecurityAuditActions.ChangeRejected);
    }

    [Test]
    public void TheSwitch_IsOffByDefault_AndMapsToItsKind()
    {
        var defaults = new ApprovalWorkflowOptions { Enabled = true };
        var on = new ApprovalWorkflowOptions { Enabled = true, CoversAdminRoleGrants = true };

        defaults.CoversAdminRoleGrants.Should().BeFalse();
        defaults.Covers(ChangeApprovalKind.AdminRoleGrant).Should().BeFalse();
        on.Covers(ChangeApprovalKind.AdminRoleGrant).Should().BeTrue();
        new ApprovalWorkflowOptions { CoversAdminRoleGrants = true }.Covers(ChangeApprovalKind.AdminRoleGrant).Should().BeFalse("the workflow is off");
    }

    private static ChangeApprovalScope LiveAdmin => ChangeApprovalScope.Approver with { IsLiveAdmin = true };

    private sealed class Harness
    {
        public Harness(bool covers = true, bool allowSelfApproval = false)
        {
            Configuration = new BeaconConfiguration
            {
                ApprovalWorkflow = new ApprovalWorkflowOptions { Enabled = true, CoversAdminRoleGrants = covers, AllowSelfApproval = allowSelfApproval }
            };
            Store.Roles.AddRange(
            [
                new BeaconRole { Id = AdminRole, Name = "Admin", Level = 3, IsSystemRole = true },
                new BeaconRole { Id = EditorRole, Name = "Editor", Level = 2, IsSystemRole = true },
                new BeaconRole { Id = ViewerRole, Name = "Viewer", Level = 1, IsSystemRole = true },
                new BeaconRole { Id = ApproverRole, Name = BeaconPermissions.Approver, Level = 0, IsSystemRole = true }
            ]);
            Store.User(AdaId, Ada, "Ada");
            Store.User(BenId, Ben, "Ben");
            Store.User(CidId, "ext-cid").SecurityStamp = "before";
            Holds(AdaId, AdminRole);
            Holds(BenId, AdminRole);
        }

        public ChangeApprovalStore Store { get; } = new();

        public BeaconConfiguration Configuration { get; }

        public RecordingSecurityAudit Audit { get; } = new();

        public void Holds(int userId, params int[] roleIds)
        {
            var user = Store.Users.Single(x => x.Id == userId);
            foreach (var roleId in roleIds)
            {
                var row = new BeaconUserRole { UserId = userId, User = user, RoleId = roleId, Role = Store.Roles.Single(x => x.Id == roleId) };
                Store.UserRoles.Add(row);
                user.UserRoles.Add(row);
            }
        }

        public ChangeApprovalRequest PendingGrant(string requester)
        {
            var proposed = $$"""{"userId":{{CidId}},"roleId":{{AdminRole}},"roleName":"Admin"}""";

            return Store.AddRequest(ChangeApprovalKind.AdminRoleGrant, CidId, AdminRoleGrantApproval.Hash(CidId, AdminRole), requester, proposedJson: proposed);
        }

        public SetUserRolesHandler SetRoles(string? caller, int callerId)
        {
            var actors = TestChangeApprovals.Caller(caller, callerId, isAdmin: true);
            var rules = new UserAdministrationRules(
                Store.Factory(),
                actors,
                TestPermissions.Admin,
                Options.Create(new RoleMappingOptions()),
                NullLogger<UserAdministrationRules>.Instance);

            return new SetUserRolesHandler(Store.Factory(), rules, actors, new FakeTimeProvider(DateTimeOffset.UtcNow), Audit, Grants(actors));
        }

        public UserManagementService Users(string? caller, ILogger<UserManagementService>? logger = null)
        {
            var hasher = new Mock<IPasswordHasher>();
            hasher
                .Setup(x => x.HashPassword(It.IsAny<string>()))
                .Returns(("hash", "salt"));

            return new UserManagementService(
                Store.Factory(),
                hasher.Object,
                Mock.Of<IRoleService>(),
                Configuration,
                TimeProvider.System,
                Audit,
                logger,
                null,
                Grants(TestChangeApprovals.Caller(caller, AdaId, isAdmin: true)));
        }

        public ChangeApprovalService Decisions(string caller, IBeaconPermissionAccessor permissions)
        {
            return new ChangeApprovalService(
                Store.Factory(),
                TestChangeApprovals.Caller(caller, caller == Ada ? AdaId : BenId, isAdmin: true),
                Mock.Of<IBeaconUserContext>(),
                Store.Protector,
                TestChangeApprovals.Hasher,
                Configuration,
                Audit,
                permissions,
                TestChangeApprovals.NoDefinitions,
                TestChangeApprovals.NoProbe,
                TestChangeApprovals.Llm(),
                NullLogger<ChangeApprovalService>.Instance);
        }

        private AdminRoleGrantApproval Grants(IBeaconActorAccessor actors)
        {
            return new AdminRoleGrantApproval(actors, Configuration, NullLogger<AdminRoleGrantApproval>.Instance);
        }
    }
}
