using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Projects.Members;
using Beacon.Core.HostData;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Npgsql;
using Beacon.Core.SecurityAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The membership and link routes: only a ProjectAdmin of the project or an Admin manages its members and
/// links; a project the caller cannot read answers 404 and one it reads without managing it 403; the last active
/// ProjectAdmin is kept by the one conditional statement that changes a row; links are read and written in one
/// serializable transaction; and every route answers 409 while the host manages membership.
/// </summary>
[TestFixture]
public class ProjectMembershipHandlersTests
{
    private const int ProjectId = 1;
    private const int MemberId = 8;

    [Test]
    public async Task SetMember_AddsANewMember_AndRecordsWhoGrantedIt()
    {
        var saved = new List<object>();
        var factory = Recording(saved, users: [new BeaconUser { Id = MemberId, ExternalId = "ext-ben", UserName = "ben", IsEnabled = true }]);

        var audit = new RecordingSecurityAudit(Caller());

        var result = await SetMember(factory, ProjectAdminScope(), audit: audit).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Editor), CancellationToken.None);

        result.Should().Be(new SetProjectMemberResult(ProjectId, MemberId, ProjectRole.Editor, true));
        saved.OfType<ProjectMember>().Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            ProjectId,
            UserId = MemberId,
            Role = ProjectRole.Editor,
            AddedByUserId = (int?)3
        });
        audit.Single("ext-ben", "ben").Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.MemberAdded, ActorUserId = (int?)3, ProjectId = (int?)ProjectId, ResourceId = ProjectId.ToString(), DetailsJson = "{\"memberUserId\":8,\"role\":\"Editor\"}" },
            x => x.ExcludingMissingMembers());
        audit.Ways.Should().Equal("staged");
    }

    [Test]
    public async Task SetMember_DemotingTheLastActiveProjectAdmin_IsRefusedByTheConditionalUpdate()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenScalar(true)
            .ThenScalar(true)
            .ThenRowsAffected(0)
            .ThenScalar(true);

        var act = () => SetMember(capture, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one active project admin*");
        var update = capture.Commands[3];
        update.Should().StartWith("UPDATE beacon.project_members");
        update.Should().Contain("EXISTS").And.Contain("is_enabled", "only another active ProjectAdmin counts");
    }

    [Test]
    public async Task SetMember_ByAnAdmin_MayLeaveNoProjectAdmin()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenScalar(true)
            .ThenScalar(true)
            .ThenRowsAffected(1);

        var result = await SetMember(capture, TestProjectAccess.Unrestricted).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);

        result.Added.Should().BeFalse();
        capture.Commands[3].Should().StartWith("UPDATE beacon.project_members").And.NotContain("EXISTS");
    }

    [Test]
    public async Task SetMember_ToAnUndefinedRole_IsABadRequest_BeforeAnyRead()
    {
        var capture = new SqlCapture();

        var act = () => SetMember(capture, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, MemberId, (ProjectRole)9), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task SetMember_OfAnInactiveOrUnknownUser_IsNotFound()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenScalar(false);

        var act = () => SetMember(capture, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task ManagingMembers_OfAProjectTheCallerCannotRead_IsNotFound_AndOfOneItOnlyReads_Forbidden()
    {
        var unreadable = () => SetMember(new SqlCapture().ThenRow(ProjectId, DBNull.Value), TestProjectAccess.ReadableIn(2))
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);
        var readOnly = () => SetMember(new SqlCapture().ThenRow(ProjectId, DBNull.Value), TestProjectAccess.ReadableIn(ProjectId))
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);
        var missing = () => SetMember(new SqlCapture().ThenNoRows(), ProjectAdminScope())
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);

        await unreadable.Should().ThrowAsync<BeaconNotFoundException>();
        await missing.Should().ThrowAsync<BeaconNotFoundException>();
        await readOnly.Should().ThrowAsync<BeaconForbiddenException>();
    }

    [Test]
    public async Task EveryMembershipRoute_Answers409_WhileTheHostManagesMembership()
    {
        var hostSource = new Mock<IProjectMembershipSource>();
        hostSource.SetupGet(x => x.IsAvailable).Returns(true);
        var capture = new SqlCapture();
        var withoutUsers = Source(userManagement: false);

        var set = () => SetMember(capture, ProjectAdminScope(), hostSource.Object).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);
        var remove = () => Remove(capture, ProjectAdminScope(), withoutUsers).Handle(new RemoveProjectMemberCommand(ProjectId, MemberId), CancellationToken.None);
        var list = () => Members(capture, ProjectAdminScope(), hostSource.Object).Handle(new GetProjectMembersQuery { ProjectId = ProjectId }, CancellationToken.None);
        var candidates = () => Candidates(capture, ProjectAdminScope(), withoutUsers, new RateLimiter()).Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "ana"), CancellationToken.None);

        await set.Should().ThrowAsync<BeaconConflictException>();
        await remove.Should().ThrowAsync<BeaconConflictException>();
        await list.Should().ThrowAsync<BeaconConflictException>();
        await candidates.Should().ThrowAsync<BeaconConflictException>();
        capture.Commands.Should().BeEmpty();
    }

    [Test]
    public async Task RemoveMember_DeletesWithTheLastProjectAdminCondition()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenRowsAffected(1);

        var audit = new RecordingSecurityAudit(Caller());

        await Remove(capture, ProjectAdminScope(), audit: audit).Handle(new RemoveProjectMemberCommand(ProjectId, MemberId), CancellationToken.None);

        var delete = capture.Commands[1];
        delete.Should().StartWith("DELETE FROM beacon.project_members");
        delete.Should().Contain("EXISTS");
        audit.Single().Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.MemberRemoved, ActorUserId = (int?)3, ProjectId = (int?)ProjectId, DetailsJson = "{\"memberUserId\":8}" },
            x => x.ExcludingMissingMembers());
        audit.Ways.Should().Equal("saved");
        capture.TransactionIsolationLevels.Should().Equal([IsolationLevel.Serializable], "the removal and its event share the serializable transaction");
    }

    [Test]
    public async Task RemoveMember_ThatIsNoMember_IsNotFound()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenRowsAffected(0)
            .ThenScalar(false);

        var act = () => Remove(capture, ProjectAdminScope()).Handle(new RemoveProjectMemberCommand(ProjectId, MemberId), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task MemberCandidates_NeedTwoCharacters_AndNeverSelectEmail()
    {
        var shortSearch = () => Candidates(new SqlCapture(), ProjectAdminScope(), Source(), new RateLimiter())
            .Handle(new SearchProjectMemberCandidatesQuery(ProjectId, " a "), CancellationToken.None);
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenNoRows();

        var result = await Candidates(capture, ProjectAdminScope(), Source(), new RateLimiter())
            .Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "an"), CancellationToken.None);

        await shortSearch.Should().ThrowAsync<InvalidOperationException>();
        result.Candidates.Should().BeEmpty();
        var sql = capture.Commands[1];
        sql.Should().NotContain("email");
        sql.Should().Contain("LIMIT @", "at most a page of candidates is read");
    }

    [Test]
    public async Task MemberCandidates_AreRateLimitedPerCaller()
    {
        var limiter = new RateLimiter();
        for (var i = 0; i < SearchProjectMemberCandidatesHandler.MaxSearchesPerMinute; i++)
        {
            limiter.IsAllowed("member-candidates:3:ext-ada", SearchProjectMemberCandidatesHandler.MaxSearchesPerMinute, TimeSpan.FromMinutes(1));
        }

        var act = () => Candidates(new SqlCapture().ThenRow(ProjectId, DBNull.Value), ProjectAdminScope(), Source(), limiter)
            .Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "ana"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconRateLimitedException>();
    }

    [Test]
    public async Task SetAccessMode_IsAnAdminsNow_AndRestrictingNeedsMemberships()
    {
        var notAdmin = () => AccessMode(new SqlCapture(), ProjectAdminScope(), Source())
            .Handle(new SetProjectAccessModeCommand(ProjectId, true), CancellationToken.None);
        var withoutMemberships = () => AccessMode(new SqlCapture(), TestProjectAccess.Unrestricted, Source(userManagement: false))
            .Handle(new SetProjectAccessModeCommand(ProjectId, true), CancellationToken.None);
        var missing = () => AccessMode(new SqlCapture().ThenNoRows(), TestProjectAccess.Unrestricted, Source())
            .Handle(new SetProjectAccessModeCommand(ProjectId, true), CancellationToken.None);

        await notAdmin.Should().ThrowAsync<BeaconForbiddenException>("a ProjectAdmin does not restrict or open a project");
        await withoutMemberships.Should().ThrowAsync<BeaconConflictException>();
        await missing.Should().ThrowAsync<BeaconNotFoundException>();
    }

    [Test]
    public async Task LinkDataSources_RefusesASourceNoOpenProjectHolds_InsideASerializableTransaction()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenRows([20, DBNull.Value, false])
            .ThenRows([20, 2, false]);
        var scope = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.ProjectAdmin, [2] = ProjectRole.Viewer },
            includesOpen: true,
            notOpen: [20]);

        var act = () => Link(capture, scope).Handle(new LinkProjectDataSourcesCommand(ProjectId, [20]), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        capture.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        capture.Commands.Should().NotContain(x => x.Contains("INSERT"), "nothing is linked");
        capture.Commits.Should().Be(0, "the refused link rolls the transaction back");
    }

    [Test]
    public async Task LinkDataSources_LinksASourceAnOpenProjectHolds_AndSkipsOnesAlreadyLinked()
    {
        var saved = new List<object>();
        var transactions = new SqlCapture();
        var open = new Project { Id = 4, Name = "open" };
        var target = new Project { Id = ProjectId, Name = "target" };
        var factory = Recording(
            saved,
            projects: [target, open],
            dataSources: [Source(10), Source(11)],
            links:
            [
                new ProjectDataSource { ProjectId = 4, DataSourceId = 10, Project = open },
                new ProjectDataSource { ProjectId = 4, DataSourceId = 11, Project = open },
                new ProjectDataSource { ProjectId = ProjectId, DataSourceId = 11, Project = target }
            ],
            offline: transactions);

        var audit = new RecordingSecurityAudit(Caller());

        var result = await Link(factory, ProjectAdminScope(), audit: audit).Handle(new LinkProjectDataSourcesCommand(ProjectId, [10, 11, 10]), CancellationToken.None);

        result.LinkedDataSourceIds.Should().Equal(10);
        saved.OfType<ProjectDataSource>().Should().ContainSingle().Which.Should().BeEquivalentTo(new { ProjectId, DataSourceId = 10, LinkedByUserId = (int?)3 });
        audit.Single().Should().BeEquivalentTo(
            new { Action = SecurityAuditActions.DataSourcesLinked, ActorUserId = (int?)3, ProjectId = (int?)ProjectId, DetailsJson = "{\"dataSourceIds\":[10],\"asDataSourceAdmin\":false}" },
            x => x.ExcludingMissingMembers());
        transactions.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        transactions.Commits.Should().Be(1);
    }

    [Test]
    public async Task LinkDataSources_WithoutAnySource_IsABadRequest()
    {
        var act = () => Link(new SqlCapture(), ProjectAdminScope()).Handle(new LinkProjectDataSourcesCommand(ProjectId, []), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Test]
    public async Task SetMember_ThatLosesTheInsertRace_ChangesTheRoleInstead()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenScalar(true)
            .ThenScalar(false)
            .ThenThrow(new PostgresException("duplicate", "ERROR", "ERROR", "23505"))
            .ThenRowsAffected(1);
        var audit = new SecurityAuditRecorder(
            capture.Factory(),
            TestProjectAccess.Actors(),
            SecurityAuditTestKit.Integrity,
            SecurityAuditTestKit.CallerHasher,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            TestRequestPrincipal.OffRequest());

        // The event save after the update is captured and stopped there (the capture cannot answer a successful insert).
        var act = () => new SetProjectMemberHandler(
                capture.Factory(),
                TestProjectAccess.Evaluator(ProjectAdminScope()),
                Source(),
                TestProjectAccess.Actors(),
                TestPermissions.None,
                Options.Create(new RoleMappingOptions()),
                new FakeTimeProvider(DateTimeOffset.UtcNow),
                audit,
                NullLogger<SetProjectMemberHandler>.Instance)
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Editor), CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<DbUpdateException>();
        thrown.Which.InnerException.Should().BeOfType<SqlCapturedException>();
        capture.Commands[3].Should().StartWith("INSERT INTO beacon.project_members").And.Contain("INSERT INTO beacon.security_audit_events", "the member and its event go in one save");
        capture.Commands[4].Should().StartWith("UPDATE beacon.project_members");
        capture.Commands[5].Should().Contain("INSERT INTO beacon.security_audit_events")
            .And.NotContain("project_members", "the lost insert and its event are not saved again with the update");
        capture.CommandParameters[5].Values.Should().Contain(SecurityAuditActions.MemberRoleChanged).And.NotContain(SecurityAuditActions.MemberAdded);
        capture.Commands.Should().HaveCount(6);
        capture.TransactionIsolationLevels.Should().Contain(IsolationLevel.Serializable);
        capture.Commits.Should().Be(0, "the role change and its event commit together");
    }

    [Test]
    public async Task SetMember_OfADisabledUser_IsNotFound()
    {
        var saved = new List<object>();
        var factory = Recording(saved, users: [new BeaconUser { Id = MemberId, ExternalId = "ext-ben", UserName = "ben", IsEnabled = false }]);

        var act = () => SetMember(factory, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconNotFoundException>();
        saved.Should().BeEmpty();
    }

    [Test]
    public async Task TheLastProjectAdmin_CannotRemoveThemself_ButAnAdminMayRemoveThem()
    {
        var members = new List<ProjectMember> { Member(3, ProjectRole.ProjectAdmin) };
        var factory = Recording([], users: [members[0].User], members: members, offline: new SqlCapture());

        var byProjectAdmin = () => Remove(factory, ProjectAdminScope()).Handle(new RemoveProjectMemberCommand(ProjectId, 3), CancellationToken.None);

        await byProjectAdmin.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one active project admin*");
        members.Should().ContainSingle();

        await Remove(factory, TestProjectAccess.Unrestricted).Handle(new RemoveProjectMemberCommand(ProjectId, 3), CancellationToken.None);

        members.Should().BeEmpty();
    }

    [Test]
    public async Task AProjectAdmin_IsRemoved_WhileAnotherEffectiveProjectAdminRemains()
    {
        var members = new List<ProjectMember> { Member(3, ProjectRole.ProjectAdmin), Member(MemberId, ProjectRole.ProjectAdmin) };
        var factory = Recording([], users: members.Select(x => x.User).ToList(), members: members, offline: new SqlCapture());

        await Remove(factory, ProjectAdminScope()).Handle(new RemoveProjectMemberCommand(ProjectId, MemberId), CancellationToken.None);

        members.Should().ContainSingle().Which.UserId.Should().Be(3);
    }

    [Test]
    public async Task TheSoleProjectAdmin_StaysAProjectAdmin_ButIsNotDemoted()
    {
        var members = new List<ProjectMember> { Member(3, ProjectRole.ProjectAdmin) };
        var factory = Recording([], users: [members[0].User], members: members, offline: new SqlCapture());

        var unchanged = await SetMember(factory, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, 3, ProjectRole.ProjectAdmin), CancellationToken.None);
        var demote = () => SetMember(factory, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, 3, ProjectRole.Editor), CancellationToken.None);

        unchanged.Added.Should().BeFalse();
        await demote.Should().ThrowAsync<InvalidOperationException>().WithMessage("*at least one active project admin*");
        members.Single().Role.Should().Be(ProjectRole.ProjectAdmin);
    }

    [Test]
    public async Task MembershipChanges_ThatConflictWithAConcurrentChange_AreAConflict()
    {
        var demote = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenScalar(true)
            .ThenScalar(true)
            .ThenThrow(new PostgresException("conflict", "ERROR", "ERROR", "40001"));
        var remove = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenThrow(new PostgresException("conflict", "ERROR", "ERROR", "40001"));

        var setRole = () => SetMember(demote, ProjectAdminScope()).Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);
        var removal = () => Remove(remove, ProjectAdminScope()).Handle(new RemoveProjectMemberCommand(ProjectId, MemberId), CancellationToken.None);

        await setRole.Should().ThrowAsync<BeaconConflictException>();
        await removal.Should().ThrowAsync<BeaconConflictException>();
        demote.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        remove.TransactionIsolationLevels.Should().Equal(IsolationLevel.Serializable);
        demote.Commits.Should().Be(0);
        remove.Commits.Should().Be(0);
    }

    [Test]
    public async Task MemberCandidates_MatchTheDisplayNameOnly_IgnoringCase()
    {
        var users = new List<BeaconUser>
        {
            new() { Id = 5, ExternalId = "ext-5", UserName = "zed", DisplayName = "Ana Lovelace", IsEnabled = true },
            new() { Id = 6, ExternalId = "ext-6", UserName = "anabel", DisplayName = null, IsEnabled = true },
            new() { Id = 7, ExternalId = "ext-7", UserName = "x", DisplayName = "Banana Split", IsEnabled = true },
            new() { Id = 8, ExternalId = "ext-8", UserName = "y", DisplayName = "Ana Disabled", IsEnabled = false }
        };
        var factory = Recording([], users: users);

        var result = await Candidates(factory, ProjectAdminScope(), Source(), new RateLimiter())
            .Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "ANA"), CancellationToken.None);

        result.Candidates.Select(x => (x.UserId, x.DisplayName)).Should().Equal((5, "Ana Lovelace"), (7, "Banana Split"));
    }

    [Test]
    public async Task MemberCandidates_ReadNeitherTheUserNameNorTheEmail()
    {
        var capture = new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenNoRows();

        await Candidates(capture, ProjectAdminScope(), Source(), new RateLimiter()).Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "an"), CancellationToken.None);

        capture.Commands[1].Should().Contain("lower(u.display_name)").And.NotContain("user_name").And.NotContain("email");
    }

    [Test]
    public async Task Members_AreNamedByDisplayName_OrANeutralLabel()
    {
        var members = new List<ProjectMember> { Member(3, ProjectRole.ProjectAdmin, displayName: "Ada Lovelace"), Member(MemberId, ProjectRole.Viewer, displayName: null) };
        var factory = Recording([], members: members);

        var page = await Members(factory, ProjectAdminScope(), Source()).Handle(new GetProjectMembersQuery { ProjectId = ProjectId }, CancellationToken.None);

        page.Items.Select(x => x.DisplayName).Should().BeEquivalentTo(["Ada Lovelace", $"User {MemberId}"]);
        page.Items.Should().NotContain(x => x.DisplayName.StartsWith("login", StringComparison.Ordinal), "the user name is never shown");
    }

    [Test]
    public async Task LinkDataSources_LinksAHostsSource_IntoTheProjectTheHostDeclaresForIt()
    {
        var saved = new List<object>();
        var hostProject = new Project { Id = ProjectId, Name = "Shop", HostManagedKey = "host:shop" };
        var factory = Recording(
            saved,
            projects: [hostProject],
            dataSources: [new DataSource { Id = 70, Name = "shop db", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc", HostManagedKey = "efcore:ShopContext" }],
            links: [],
            offline: new SqlCapture());
        var registry = new TestHostDataSources(
            new HostDataSourceRegistration(typeof(ShopContext), new HostDbContextOptions { ProjectName = "Shop", ReadOnlyConnectionStringName = "ShopReadOnly" }));

        var result = await Link(factory, TestProjectAccess.Unrestricted, registry).Handle(new LinkProjectDataSourcesCommand(ProjectId, [70]), CancellationToken.None);

        result.LinkedDataSourceIds.Should().Equal(70);
    }

    [Test]
    public async Task LinkDataSources_ThatLoseARace_AreAConflict_AndNothingCommits()
    {
        var duplicate = LinkScript().ThenThrow(new PostgresException("duplicate", "ERROR", "ERROR", "23505"));
        var serialization = LinkScript().ThenThrow(new PostgresException("conflict", "ERROR", "ERROR", "40001"));

        var linkedConcurrently = () => Link(duplicate, ProjectAdminScope()).Handle(new LinkProjectDataSourcesCommand(ProjectId, [10]), CancellationToken.None);
        var conflicting = () => Link(serialization, ProjectAdminScope()).Handle(new LinkProjectDataSourcesCommand(ProjectId, [10]), CancellationToken.None);

        await linkedConcurrently.Should().ThrowAsync<BeaconConflictException>();
        await conflicting.Should().ThrowAsync<BeaconConflictException>();
        duplicate.Commits.Should().Be(0);
        serialization.Commits.Should().Be(0);
    }

    [Test]
    public async Task AUserAdmin_ManagesTheMembersOfAProjectItCannotRead_AndTheChangeIsLogged()
    {
        var saved = new List<object>();
        var logs = new LogRecorder();
        var factory = Recording(saved, users: [new BeaconUser { Id = MemberId, ExternalId = "ext-ben", UserName = "ben", IsEnabled = true }]);
        var handler = new SetProjectMemberHandler(
            factory,
            TestProjectAccess.Evaluator(ProjectAccessScope.None),
            Source(),
            TestProjectAccess.Actors(),
            TestPermissions.Holding(BeaconPermissions.UserAdmin),
            Options.Create(new RoleMappingOptions()),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            new RecordingSecurityAudit(),
            logs.For<SetProjectMemberHandler>());

        var result = await handler.Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.ProjectAdmin), CancellationToken.None);

        result.Added.Should().BeTrue();
        saved.OfType<ProjectMember>().Should().ContainSingle().Which.AddedByUserId.Should().Be(3);
        logs.Entries.Should().ContainSingle()
            .Which.Message.Should().Be($"Project member added: project {ProjectId}, user {MemberId}, role ProjectAdmin, by user 3");
    }

    [TestCase("Admin", RoleService.RoleLevels.Admin, false)]
    [TestCase("UserAdmin", RoleService.RoleLevels.Functional, false)]
    [TestCase("Auditor", RoleService.RoleLevels.Functional, false)]
    [TestCase("Former functional role", RoleService.RoleLevels.Functional, false)]
    [TestCase("Viewer", RoleService.RoleLevels.Viewer, true)]
    public async Task AUserAdmin_ChangesNoProjectMembershipOfAUserOnlyAnAdminMayChange(string roleName, int level, bool allowed)
    {
        var saved = new List<object>();
        var member = new BeaconUser
        {
            Id = MemberId,
            ExternalId = "ext-ben",
            UserName = "ben",
            IsEnabled = true,
            UserRoles = [new BeaconUserRole { UserId = MemberId, Role = new BeaconRole { Name = roleName, Level = level, IsSystemRole = true } }]
        };
        var factory = Recording(saved, users: [member], members: [Member(MemberId, ProjectRole.Viewer)]);
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var add = () => SetMember(factory, ProjectAccessScope.None, permissions: userAdmin)
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.ProjectAdmin), CancellationToken.None);
        var remove = () => Remove(factory, ProjectAccessScope.None, permissions: userAdmin)
            .Handle(new RemoveProjectMemberCommand(ProjectId, MemberId), CancellationToken.None);

        if (allowed)
        {
            await add.Should().NotThrowAsync();
            return;
        }

        await add.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ProjectMembershipRules.ProtectedMemberMessage);
        await remove.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ProjectMembershipRules.ProtectedMemberMessage);
        saved.OfType<ProjectMember>().Should().BeEmpty();
    }

    [Test]
    public async Task AUserAdmin_ChangesNoProjectMembershipOfASuperAdmin_ButAProjectAdminOfTheProjectMay()
    {
        var factory = Recording([], users: [new BeaconUser { Id = MemberId, ExternalId = "ext-root", UserName = "root", IsEnabled = true, IsSuperAdmin = true }]);
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var asUserAdmin = () => SetMember(factory, ProjectAccessScope.None, permissions: userAdmin)
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);
        var asProjectAdmin = () => SetMember(factory, ProjectAdminScope(), permissions: userAdmin)
            .Handle(new SetProjectMemberCommand(ProjectId, MemberId, ProjectRole.Viewer), CancellationToken.None);

        await asUserAdmin.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ProjectMembershipRules.ProtectedMemberMessage);
        await asProjectAdmin.Should().NotThrowAsync("a ProjectAdmin manages its own project's members");
    }

    [Test]
    public async Task OpeningARestrictedProject_IsAnAdmins_WhileAUserAdminRestrictsOne()
    {
        var restricted = new Project { Id = ProjectId, Name = "restricted", RequiresMembership = true };
        var open = new Project { Id = 7, Name = "open" };
        var factory = Recording([], projects: [restricted, open]);
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var opened = () => AccessMode(factory, ProjectAccessScope.None, userAdmin)
            .Handle(new SetProjectAccessModeCommand(ProjectId, false), CancellationToken.None);
        await opened.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(SetProjectAccessModeHandler.OpenRequiresAdminMessage);
        restricted.RequiresMembership.Should().BeTrue();

        var restrictedByUserAdmin = await AccessMode(factory, ProjectAccessScope.None, userAdmin)
            .Handle(new SetProjectAccessModeCommand(7, true), CancellationToken.None);
        var openedByAdmin = await AccessMode(factory, ProjectAccessScope.Unrestricted, TestPermissions.Admin)
            .Handle(new SetProjectAccessModeCommand(ProjectId, false), CancellationToken.None);

        restrictedByUserAdmin.RequiresMembership.Should().BeTrue();
        openedByAdmin.RequiresMembership.Should().BeFalse();
    }

    [Test]
    public async Task AUserAdmin_ListsTheMembersAndCandidates_OfAProjectItCannotRead()
    {
        var factory = Recording(
            [],
            users: [new BeaconUser { Id = MemberId, ExternalId = "ext-ben", UserName = "ben", DisplayName = "Ben", IsEnabled = true }],
            members: [Member(MemberId, ProjectRole.Editor, "Ben")]);
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var members = await Members(factory, ProjectAccessScope.None, Source(), userAdmin).Handle(new GetProjectMembersQuery { ProjectId = ProjectId }, CancellationToken.None);
        var candidates = await Candidates(factory, ProjectAccessScope.None, Source(), new RateLimiter(), userAdmin).Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "be"), CancellationToken.None);
        var withoutPermission = () => Members(factory, ProjectAccessScope.None, Source()).Handle(new GetProjectMembersQuery { ProjectId = ProjectId }, CancellationToken.None);

        members.Items.Should().ContainSingle().Which.UserId.Should().Be(MemberId);
        candidates.Candidates.Should().ContainSingle().Which.Should().Be(new MemberCandidate(MemberId, "Ben", true, true));
        await withoutPermission.Should().ThrowAsync<BeaconNotFoundException>("without the permission the project is unreadable");
    }

    [Test]
    public async Task TheMembersAndCandidates_SayWhichMembershipsAUserAdminMayNotChange()
    {
        // The caller (TestProjectAccess.Actors) is Beacon user 3; Ben holds a functional role, Bert a built-in one.
        const int bertId = 9;
        var auditorRole = new BeaconRole { Name = BeaconPermissions.Auditor, Level = RoleService.RoleLevels.Functional, IsSystemRole = true };
        var viewerRole = new BeaconRole { Name = RoleService.RoleNames.Viewer, Level = RoleService.RoleLevels.Viewer, IsSystemRole = true };
        var users = new List<BeaconUser>
        {
            new() { Id = 3, ExternalId = "ext-ada", UserName = "ada", DisplayName = "Bess", IsEnabled = true },
            new() { Id = MemberId, ExternalId = "ext-ben", UserName = "ben", DisplayName = "Ben", IsEnabled = true, UserRoles = [new BeaconUserRole { UserId = MemberId, Role = auditorRole }] },
            new() { Id = bertId, ExternalId = "ext-bert", UserName = "bert", DisplayName = "Bert", IsEnabled = true, UserRoles = [new BeaconUserRole { UserId = bertId, Role = viewerRole }] }
        };
        var factory = Recording(
            [],
            users: users,
            members: [Member(3, ProjectRole.Viewer, "Bess"), Member(MemberId, ProjectRole.Editor, "Ben"), Member(bertId, ProjectRole.Viewer, "Bert")]);
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var asUserAdmin = await Members(factory, ProjectAccessScope.None, Source(), userAdmin).Handle(new GetProjectMembersQuery { ProjectId = ProjectId }, CancellationToken.None);
        var candidatesAsUserAdmin = await Candidates(factory, ProjectAccessScope.None, Source(), new RateLimiter(), userAdmin).Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "be"), CancellationToken.None);
        var asProjectAdmin = await Members(factory, ProjectAdminScope(), Source(), userAdmin).Handle(new GetProjectMembersQuery { ProjectId = ProjectId }, CancellationToken.None);
        var candidatesAsProjectAdmin = await Candidates(factory, ProjectAdminScope(), Source(), new RateLimiter(), userAdmin).Handle(new SearchProjectMemberCandidatesQuery(ProjectId, "be"), CancellationToken.None);

        asUserAdmin.Items.ToDictionary(x => x.UserId, x => x.CanChange).Should().Equal(new Dictionary<int, bool> { [3] = false, [MemberId] = false, [bertId] = true });
        candidatesAsUserAdmin.Candidates.ToDictionary(x => x.UserId, x => x.CanAdd).Should().Equal(new Dictionary<int, bool> { [3] = false, [MemberId] = false, [bertId] = true });
        asProjectAdmin.Items.Should().OnlyContain(x => x.CanChange, "a ProjectAdmin manages its own project's members");
        candidatesAsProjectAdmin.Candidates.Should().OnlyContain(x => x.CanAdd);
    }

    [Test]
    public async Task AUserAdmin_CannotChangeTheirOwnMembership()
    {
        // The caller (TestProjectAccess.Actors) is Beacon user 3.
        var users = new List<BeaconUser> { new() { Id = 3, ExternalId = "ext-ada", UserName = "ada", IsEnabled = true } };
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var join = () => SetMember(Recording([], users: users), ProjectAccessScope.None, permissions: userAdmin)
            .Handle(new SetProjectMemberCommand(ProjectId, 3, ProjectRole.ProjectAdmin), CancellationToken.None);
        var leave = () => Remove(Recording([], users: users, offline: new SqlCapture()), ProjectAccessScope.None, permissions: userAdmin)
            .Handle(new RemoveProjectMemberCommand(ProjectId, 3), CancellationToken.None);

        await join.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*own project membership*");
        await leave.Should().ThrowAsync<BeaconForbiddenException>().WithMessage("*own project membership*");
    }

    [Test]
    public async Task AUserAdmin_RestrictsOrOpensAnyProject_ButLinksNoDataSource()
    {
        var userAdmin = TestPermissions.Holding(BeaconPermissions.UserAdmin);

        var restrict = () => AccessMode(new SqlCapture().ThenNoRows(), ProjectAccessScope.None, Source(), userAdmin)
            .Handle(new SetProjectAccessModeCommand(ProjectId, true), CancellationToken.None);
        var link = () => Link(Recording([], dataSources: [Source(10)], offline: new SqlCapture()), ProjectAccessScope.None, permissions: userAdmin)
            .Handle(new LinkProjectDataSourcesCommand(ProjectId, [10]), CancellationToken.None);

        await restrict.Should().ThrowAsync<BeaconNotFoundException>("past the permission check, the project is looked up (and missing here)");
        await link.Should().ThrowAsync<BeaconNotFoundException>("linking needs ManageMembers in the project, which the permission does not give");
    }

    [Test]
    public async Task ADataSourceAdmin_LinksASourceOnlyARestrictedProjectItReadsHolds_AndGainsNoMembership()
    {
        var saved = new List<object>();
        var restricted = new Project { Id = 2, Name = "restricted", RequiresMembership = true };
        var factory = Recording(
            saved,
            projects: [new Project { Id = ProjectId, Name = "target" }, restricted],
            dataSources: [Source(20)],
            links: [new ProjectDataSource { ProjectId = 2, DataSourceId = 20, Project = restricted }],
            offline: new SqlCapture());
        var readsRestricted = TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.ProjectAdmin, [2] = ProjectRole.Viewer },
            includesOpen: true,
            notOpen: []);

        var result = await Link(factory, readsRestricted, permissions: TestPermissions.Holding(BeaconPermissions.DataSourceAdmin))
            .Handle(new LinkProjectDataSourcesCommand(ProjectId, [20]), CancellationToken.None);
        var withoutPermission = () => Link(factory, readsRestricted)
            .Handle(new LinkProjectDataSourcesCommand(ProjectId, [20]), CancellationToken.None);

        result.LinkedDataSourceIds.Should().Equal(20);
        saved.OfType<ProjectMember>().Should().BeEmpty("linking a restricted project's source grants no membership of it");
        await withoutPermission.Should().ThrowAsync<BeaconForbiddenException>("only an Admin or a DataSourceAdmin links a source no open project holds");
    }

    [Test]
    public async Task ADataSourceAdmin_CannotLinkASourceItCannotRead_IntoAProjectItIsAMemberOf()
    {
        var saved = new List<object>();
        var restricted = new Project { Id = 2, Name = "restricted", RequiresMembership = true };
        var factory = Recording(
            saved,
            projects: [new Project { Id = ProjectId, Name = "target", RequiresMembership = true }, restricted],
            dataSources: [Source(20), Source(30)],
            links: [new ProjectDataSource { ProjectId = 2, DataSourceId = 20, Project = restricted }],
            offline: new SqlCapture());

        var restrictedOnly = () => Link(factory, ProjectAdminScope(), permissions: TestPermissions.Holding(BeaconPermissions.DataSourceAdmin))
            .Handle(new LinkProjectDataSourcesCommand(ProjectId, [20]), CancellationToken.None);
        var unlinked = () => Link(factory, ProjectAdminScope(), permissions: TestPermissions.Holding(BeaconPermissions.DataSourceAdmin))
            .Handle(new LinkProjectDataSourcesCommand(ProjectId, [30]), CancellationToken.None);

        await restrictedOnly.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ProjectDataSourceLinks.UnreadableSourceMessage);
        await unlinked.Should().ThrowAsync<BeaconForbiddenException>().WithMessage(ProjectDataSourceLinks.UnreadableSourceMessage);
        saved.OfType<ProjectDataSource>().Should().BeEmpty("the members of the target project would read the source");
    }

    // The target project, data source 10 (ordinary, closed when unlinked), held by the live open project 4.
    private static SqlCapture LinkScript()
    {
        return new SqlCapture()
            .ThenRow(ProjectId, DBNull.Value)
            .ThenRows([10, DBNull.Value, false])
            .ThenRows([10, 4, true]);
    }

    private static ProjectMember Member(int userId, ProjectRole role, string? displayName = "someone")
    {
        var user = new BeaconUser
        {
            Id = userId,
            ExternalId = $"ext-{userId}",
            UserName = $"login{userId}",
            DisplayName = displayName,
            IsEnabled = true,
            UserRoles = [new BeaconUserRole { UserId = userId, Role = new BeaconRole { Name = "Editor", Level = 2 } }]
        };

        return new ProjectMember { ProjectId = ProjectId, UserId = userId, Role = role, User = user };
    }

    private static IDbContextFactory<BeaconContext> Recording(
        List<object> saved,
        List<BeaconUser>? users = null,
        List<Project>? projects = null,
        List<DataSource>? dataSources = null,
        List<ProjectDataSource>? links = null,
        SqlCapture? offline = null,
        List<ProjectMember>? members = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users ?? [], saved),
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects ?? [new Project { Id = ProjectId, Name = "p" }], saved),
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet(dataSources ?? [], saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links ?? [], saved),
                    [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(members ?? [], saved)
                },
                saved,
                offline ?? new SqlCapture()));

        return factory.Object;
    }

    private static DataSource Source(int id)
    {
        return new DataSource { Id = id, Name = $"source {id}", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" };
    }

    private static ProjectAccessScope ProjectAdminScope()
    {
        return TestProjectAccess.Scope(
            new Dictionary<int, ProjectRole> { [ProjectId] = ProjectRole.ProjectAdmin, [4] = ProjectRole.Editor },
            includesOpen: true,
            notOpen: []);
    }

    private static BeaconActor Caller()
    {
        return new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 3, IsActiveUser = true };
    }

    private static IProjectMembershipSource Source(bool userManagement = true)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;

        return new DatabaseProjectMembershipSource(configuration, new SqlCapture().Factory());
    }

    private static SetProjectMemberHandler SetMember(SqlCapture capture, ProjectAccessScope scope, IProjectMembershipSource? source = null, IBeaconPermissionAccessor? permissions = null, RecordingSecurityAudit? audit = null)
    {
        return SetMember(capture.Factory(), scope, source, permissions, audit);
    }

    private static SetProjectMemberHandler SetMember(IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope, IProjectMembershipSource? source = null, IBeaconPermissionAccessor? permissions = null, RecordingSecurityAudit? audit = null)
    {
        return new SetProjectMemberHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            source ?? Source(),
            TestProjectAccess.Actors(),
            permissions ?? TestPermissions.None,
            Options.Create(new RoleMappingOptions()),
            new FakeTimeProvider(DateTimeOffset.UtcNow),
            audit ?? new RecordingSecurityAudit(),
            NullLogger<SetProjectMemberHandler>.Instance);
    }

    private static RemoveProjectMemberHandler Remove(SqlCapture capture, ProjectAccessScope scope, IProjectMembershipSource? source = null, IBeaconPermissionAccessor? permissions = null, RecordingSecurityAudit? audit = null)
    {
        return Remove(capture.Factory(), scope, source, permissions, audit);
    }

    private static RemoveProjectMemberHandler Remove(IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope, IProjectMembershipSource? source = null, IBeaconPermissionAccessor? permissions = null, RecordingSecurityAudit? audit = null)
    {
        return new RemoveProjectMemberHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            source ?? Source(),
            TestProjectAccess.Actors(),
            permissions ?? TestPermissions.None,
            Options.Create(new RoleMappingOptions()),
            audit ?? new RecordingSecurityAudit(),
            NullLogger<RemoveProjectMemberHandler>.Instance);
    }

    private static GetProjectMembersHandler Members(SqlCapture capture, ProjectAccessScope scope, IProjectMembershipSource source)
    {
        return Members(capture.Factory(), scope, source);
    }

    private static GetProjectMembersHandler Members(IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope, IProjectMembershipSource source, IBeaconPermissionAccessor? permissions = null)
    {
        return new GetProjectMembersHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            source,
            TestProjectAccess.Actors(),
            permissions ?? TestPermissions.None,
            Options.Create(new RoleMappingOptions()),
            NullLogger<GetProjectMembersHandler>.Instance);
    }

    private static SearchProjectMemberCandidatesHandler Candidates(
        SqlCapture capture,
        ProjectAccessScope scope,
        IProjectMembershipSource source,
        RateLimiter limiter)
    {
        return Candidates(capture.Factory(), scope, source, limiter);
    }

    private static SearchProjectMemberCandidatesHandler Candidates(
        IDbContextFactory<BeaconContext> factory,
        ProjectAccessScope scope,
        IProjectMembershipSource source,
        RateLimiter limiter,
        IBeaconPermissionAccessor? permissions = null)
    {
        return new SearchProjectMemberCandidatesHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            source,
            TestProjectAccess.Actors(),
            permissions ?? TestPermissions.None,
            limiter,
            Options.Create(new RoleMappingOptions()),
            NullLogger<SearchProjectMemberCandidatesHandler>.Instance);
    }

    private static SetProjectAccessModeHandler AccessMode(IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope, IBeaconPermissionAccessor permissions)
    {
        return new SetProjectAccessModeHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            Source(),
            TestProjectAccess.Actors(),
            permissions,
            Options.Create(new ProjectAuthorizationOptions()),
            new RecordingSecurityAudit(),
            NullLogger<SetProjectAccessModeHandler>.Instance);
    }

    private static SetProjectAccessModeHandler AccessMode(SqlCapture capture, ProjectAccessScope scope, IProjectMembershipSource source, IBeaconPermissionAccessor? permissions = null, RecordingSecurityAudit? audit = null)
    {
        return new SetProjectAccessModeHandler(
            capture.Factory(),
            TestProjectAccess.Evaluator(scope),
            source,
            TestProjectAccess.Actors(),
            permissions ?? TestPermissions.None,
            Options.Create(new ProjectAuthorizationOptions()),
            audit ?? new RecordingSecurityAudit(),
            NullLogger<SetProjectAccessModeHandler>.Instance);
    }

    private static LinkProjectDataSourcesHandler Link(SqlCapture capture, ProjectAccessScope scope)
    {
        return Link(capture.Factory(), scope);
    }

    private static LinkProjectDataSourcesHandler Link(IDbContextFactory<BeaconContext> factory, ProjectAccessScope scope, IHostDataSourceRegistry? hostSources = null, IBeaconPermissionAccessor? permissions = null, RecordingSecurityAudit? audit = null)
    {
        return new LinkProjectDataSourcesHandler(
            factory,
            TestProjectAccess.Evaluator(scope),
            TestProjectAccess.Actors(),
            permissions ?? TestPermissions.None,
            hostSources ?? new TestHostDataSources(),
            Options.Create(new ProjectAuthorizationOptions()),
            audit ?? new RecordingSecurityAudit(),
            NullLogger<LinkProjectDataSourcesHandler>.Instance);
    }

    private sealed class ShopContext;
}
