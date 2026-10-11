using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The project access evaluator's decisions per kind of caller: Admins and work without a request are
/// unrestricted; signed-in users reach open projects at their global role and restricted ones through membership, the
/// lower of the two roles; archived projects grant nothing; API keys act as their owner without the Admin bypass and
/// within their list; MCP callers by their configured projects and kind; owners of scheduled work by their own access.
/// The deployment: project 1 open, 2 restricted, 3 archived (open), 4 open; data source 10 in project 1, 20 only in
/// project 2, 30 only in archived project 3, 40 unlinked and left open, 50 unlinked and closed, 60 in projects 1 and 2.
/// </summary>
[TestFixture]
public class ProjectAccessEvaluatorTests
{
    private const int OpenProject = 1;
    private const int RestrictedProject = 2;
    private const int ArchivedProject = 3;
    private const int OtherOpenProject = 4;
    private const int MemberUserId = 7;

    private List<Project> _projects = null!;
    private List<DataSource> _dataSources = null!;
    private List<ProjectDataSource> _links = null!;
    private List<ProjectMember> _members = null!;
    private int _contextsCreated;

    [SetUp]
    public void SetUp()
    {
        _projects =
        [
            new Project { Id = OpenProject, Name = "open" },
            new Project { Id = RestrictedProject, Name = "restricted", RequiresMembership = true },
            new Project { Id = ArchivedProject, Name = "archived", ArchivedTime = DateTime.UtcNow },
            new Project { Id = OtherOpenProject, Name = "other" }
        ];
        _dataSources =
        [
            Source(10),
            Source(20),
            Source(30),
            Source(40, openWhenUnlinked: true),
            Source(50, openWhenUnlinked: false),
            Source(60)
        ];
        _links = [Link(OpenProject, 10), Link(RestrictedProject, 20), Link(ArchivedProject, 30), Link(OpenProject, 60), Link(RestrictedProject, 60)];
        _members = [];
        _contextsCreated = 0;
    }

    [Test]
    public async Task WorkWithoutARequest_IsUnrestricted()
    {
        var scope = await Evaluate(BeaconActor.OffRequest);

        scope.IsUnrestricted.Should().BeTrue();
        _contextsCreated.Should().Be(0, "no snapshot is read for an unrestricted caller");
    }

    [Test]
    public async Task AnAnonymousCaller_GetsNothing()
    {
        var scope = await Evaluate(BeaconActor.Nobody);

        scope.Should().BeSameAs(ProjectAccessScope.None);
        scope.For(ProjectAction.Read).Ids.Should().BeEmpty();
        scope.For(ProjectAction.Read).IncludesOpen.Should().BeFalse();
    }

    [Test]
    public async Task AUserWhoIsAnAdminNow_IsUnrestricted_EvenWithoutTheRoleClaim()
    {
        var scope = await Evaluate(User(isLiveAdmin: true, holdsAdminClaim: false));

        scope.IsUnrestricted.Should().BeTrue();
    }

    [Test]
    public async Task AStaleAdminClaim_OfAUserWhoIsNoAdminNow_GrantsNoBypass()
    {
        var scope = await Evaluate(User(isLiveAdmin: false, holdsAdminClaim: true));

        scope.IsUnrestricted.Should().BeFalse();
        scope.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse();
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task ASessionWithoutAStoredUser_HonoursItsAdminClaim_OnlyWithoutUserManagement(bool userManagement, bool unrestricted)
    {
        var scope = await Evaluate(new BeaconActor("host-user", true) { Kind = BeaconPrincipalKind.Session }, userManagement: userManagement);

        scope.IsUnrestricted.Should().Be(unrestricted);
        if (!unrestricted)
        {
            scope.Should().BeSameAs(ProjectAccessScope.None, "with user management on, every caller has a stored user");
        }
    }

    [Test]
    public async Task ANonAdminSessionWithoutAStoredUser_GetsMembershipsOnlyFromAHostSource()
    {
        var session = new BeaconActor("host-user", false) { Kind = BeaconPrincipalKind.Session };
        var host = HostSource(new Dictionary<int, ProjectRole> { [RestrictedProject] = ProjectRole.Viewer });

        var withDatabaseSource = await Evaluate(session, userManagement: false);
        var withHostSource = await Evaluate(session, userManagement: false, membershipSource: host.Object);

        withDatabaseSource.Allows(OpenProject, ProjectAction.Write).Should().BeTrue();
        withDatabaseSource.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse("the database source has no memberships for a session without a user");
        withHostSource.Allows(RestrictedProject, ProjectAction.Read).Should().BeTrue();
        withHostSource.Allows(RestrictedProject, ProjectAction.Write).Should().BeFalse("its membership is Viewer");
    }

    [Test]
    public async Task AnInactiveUser_GetsNothing()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(User() with { IsActiveUser = false });

        scope.Should().BeSameAs(ProjectAccessScope.None);
    }

    [Test]
    public async Task AnEditor_ReachesOpenProjectsAsAnEditor_AndTheOpenlyReachableSources()
    {
        var scope = await Evaluate(User());

        scope.For(ProjectAction.Write).Ids.Should().Equal(OpenProject, OtherOpenProject);
        scope.For(ProjectAction.Write).IncludesOpen.Should().BeTrue();
        scope.For(ProjectAction.Read).NotOpenDataSourceIds.Should().Equal(20, 30, 50);
        scope.Allows(OpenProject, ProjectAction.Manage).Should().BeTrue("an Editor manages in an open project, as before");
        scope.Allows(OpenProject, ProjectAction.ManageMembers).Should().BeFalse("membership is never an open grant");
        scope.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse("a non-member does not reach a restricted project");
        scope.Allows(ArchivedProject, ProjectAction.Read).Should().BeFalse("an archived project grants nothing");
        scope.RoleIn(OpenProject).Should().Be(ProjectRole.Editor);
    }

    [Test]
    public async Task AViewer_ReadsOpenProjects_ButWritesNowhere()
    {
        var scope = await Evaluate(User(), ceiling: Ceiling.Viewer);

        scope.For(ProjectAction.Read).Ids.Should().Equal(OpenProject, OtherOpenProject);
        scope.For(ProjectAction.Write).Ids.Should().BeEmpty();
        scope.For(ProjectAction.Write).IncludesOpen.Should().BeFalse();
        scope.For(ProjectAction.Run).IncludesOpen.Should().BeTrue();
    }

    [Test]
    public async Task ACallerWithNeitherReadNorWrite_GetsNothing()
    {
        var scope = await Evaluate(User(), ceiling: Ceiling.None);

        scope.Should().BeSameAs(ProjectAccessScope.None);
    }

    [TestCase(ProjectRole.Viewer, true, false, false, false)]
    [TestCase(ProjectRole.Editor, true, true, false, false)]
    [TestCase(ProjectRole.ProjectAdmin, true, true, true, true)]
    public async Task AMemberOfARestrictedProject_GetsTheActionsOfItsRole(
        ProjectRole role, bool read, bool write, bool manage, bool manageMembers)
    {
        _members.Add(Member(RestrictedProject, role));

        var scope = await Evaluate(User());

        scope.Allows(RestrictedProject, ProjectAction.Read).Should().Be(read);
        scope.Allows(RestrictedProject, ProjectAction.Run).Should().Be(read);
        scope.Allows(RestrictedProject, ProjectAction.Write).Should().Be(write);
        scope.Allows(RestrictedProject, ProjectAction.Query).Should().Be(write);
        scope.Allows(RestrictedProject, ProjectAction.Manage).Should().Be(manage);
        scope.Allows(RestrictedProject, ProjectAction.ManageMembers).Should().Be(manageMembers);
        scope.RoleIn(RestrictedProject).Should().Be(role);
    }

    [Test]
    public async Task AnEditorMember_OfARestrictedProject_ManagesOnlyAsTheRecordedOwner()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.Editor));

        var scope = await Evaluate(User());

        scope.Allows(RestrictedProject, ProjectAction.Manage).Should().BeFalse();
        scope.Allows(RestrictedProject, ProjectAction.Manage, asRecordedOwner: true).Should().BeTrue();
        scope.For(ProjectAction.Manage, asRecordedOwner: true).Ids.Should().Contain(RestrictedProject);
    }

    [Test]
    public async Task AViewerCeiling_CapsAProjectAdminMembership()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(User(), ceiling: Ceiling.Viewer);

        scope.RoleIn(RestrictedProject).Should().Be(ProjectRole.Viewer);
        scope.Allows(RestrictedProject, ProjectAction.Write).Should().BeFalse();
        scope.Allows(RestrictedProject, ProjectAction.ManageMembers).Should().BeFalse();
    }

    [Test]
    public async Task AProjectAdminOfAnOpenProject_ManagesItsMembers_AndTheOpenGrantStaysForTheRest()
    {
        _members.Add(Member(OpenProject, ProjectRole.ProjectAdmin));
        _members.Add(Member(OtherOpenProject, ProjectRole.Viewer));

        var scope = await Evaluate(User());

        scope.For(ProjectAction.ManageMembers).Ids.Should().Equal(OpenProject);
        scope.RoleIn(OtherOpenProject).Should().Be(ProjectRole.Editor, "the higher of the open grant and the membership wins");
    }

    [Test]
    public async Task AMembershipOfAnArchivedProject_GrantsNothing()
    {
        _members.Add(Member(ArchivedProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(User());

        scope.Allows(ArchivedProject, ProjectAction.Read).Should().BeFalse();
        scope.For(ProjectAction.Read).Ids.Should().NotContain(ArchivedProject);
    }

    [Test]
    public async Task WithEveryProjectOpenAndEverySourceReachable_EachActionIsAllowedEverywhere_ButMembersStayMembershipOnly()
    {
        _projects.RemoveAll(x => x.Id is RestrictedProject or ArchivedProject);
        _links.RemoveAll(x => x.ProjectId is RestrictedProject or ArchivedProject);
        _dataSources.RemoveAll(x => x.Id is 20 or 30 or 50);
        _members.Add(Member(OpenProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(User());

        scope.For(ProjectAction.Read).Should().BeSameAs(ProjectSet.All, "list filters add nothing to the SQL then");
        scope.For(ProjectAction.Write).Should().BeSameAs(ProjectSet.All);
        scope.For(ProjectAction.ManageMembers).Ids.Should().Equal(OpenProject);
        scope.For(ProjectAction.ManageMembers).IncludesOpen.Should().BeFalse();
        scope.Allows(99, ProjectAction.Read).Should().BeFalse("a project that does not exist is never allowed");
    }

    [Test]
    public async Task OneUnlinkedClosedSource_KeepsEveryoneOffTheFastPath()
    {
        _projects.RemoveAll(x => x.Id is RestrictedProject or ArchivedProject);
        _links.RemoveAll(x => x.ProjectId is RestrictedProject or ArchivedProject);
        _dataSources.RemoveAll(x => x.Id is 20 or 30);

        var scope = await Evaluate(User());

        scope.For(ProjectAction.Read).Should().NotBeSameAs(ProjectSet.All);
        scope.For(ProjectAction.Read).Ids.Should().Equal(OpenProject, OtherOpenProject);
        scope.For(ProjectAction.Read).NotOpenDataSourceIds.Should().Equal(50);
    }

    [Test]
    public async Task AnArchivedProject_KeepsEveryoneOffTheFastPath()
    {
        _projects.RemoveAll(x => x.Id is RestrictedProject);
        _links.RemoveAll(x => x.ProjectId is RestrictedProject or ArchivedProject);
        _dataSources.RemoveAll(x => x.Id is 20 or 30 or 50);

        var scope = await Evaluate(User());

        scope.For(ProjectAction.Read).Ids.Should().Equal(OpenProject, OtherOpenProject);
        scope.For(ProjectAction.Read).NotOpenDataSourceIds.Should().BeEmpty();
    }

    [Test]
    public async Task ACaller_ReachesTheQueriesWithoutStepsItOwns_ForReadAndWrite_UnlessConfinedToAList()
    {
        var editor = await Evaluate(User());
        var viewer = await Evaluate(User(), ceiling: Ceiling.Viewer);
        var listedKey = await Evaluate(Key(projects: [OpenProject]));

        editor.For(ProjectAction.Read).QueryOwnerUserId.Should().Be("ext-member");
        editor.For(ProjectAction.Write).QueryOwnerUserId.Should().Be("ext-member");
        editor.For(ProjectAction.Run).QueryOwnerUserId.Should().BeNull();
        editor.For(ProjectAction.Manage).QueryOwnerUserId.Should().BeNull();
        editor.For(ProjectAction.Read).IsConfinedToList.Should().BeFalse();
        viewer.For(ProjectAction.Read).QueryOwnerUserId.Should().Be("ext-member");
        viewer.For(ProjectAction.Write).QueryOwnerUserId.Should().BeNull("a reader writes nowhere, its own queries included");
        listedKey.For(ProjectAction.Read).IsConfinedToList.Should().BeTrue();
        listedKey.For(ProjectAction.Read).QueryOwnerUserId.Should().BeNull("a caller confined to a list reaches no query outside it");
    }

    [Test]
    public async Task AHostMembershipWithAnUnknownRole_GrantsNothing()
    {
        var host = HostSource(new Dictionary<int, ProjectRole> { [RestrictedProject] = (ProjectRole)99, [OtherOpenProject] = ProjectRole.ProjectAdmin });

        var scope = await Evaluate(User(), membershipSource: host.Object);

        scope.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse();
        scope.Allows(OtherOpenProject, ProjectAction.ManageMembers).Should().BeTrue("the known memberships still count");
    }

    [Test]
    public async Task AnotherActorsScope_NeverReusesTheCurrentCallersMemberships()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.ProjectAdmin));
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(User());
        var evaluator = Build(accessor.Object, Ceiling.Editor, requireMembership: false, userManagement: true, membershipSource: null, ownerCeiling: ProjectRole.Editor);
        var otherOwner = Owner() with { UserId = "ext-other", BeaconUserId = MemberUserId + 1 };

        var current = await evaluator.GetScopeAsync(CancellationToken.None);
        var other = await evaluator.GetScopeAsync(otherOwner, CancellationToken.None);
        var currentAgain = await evaluator.GetScopeAsync(CancellationToken.None);

        current.Allows(RestrictedProject, ProjectAction.Read).Should().BeTrue();
        other.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse("the other actor is no member");
        other.For(ProjectAction.Read).QueryOwnerUserId.Should().Be("ext-other");
        currentAgain.Should().BeSameAs(current);
    }

    [Test]
    public async Task RequiringMembershipEverywhere_ClosesOpenProjectsAndSources()
    {
        _members.Add(Member(OpenProject, ProjectRole.Viewer));

        var scope = await Evaluate(User(), requireMembership: true);

        scope.For(ProjectAction.Read).Ids.Should().Equal(OpenProject);
        scope.For(ProjectAction.Read).IncludesOpen.Should().BeFalse();
        scope.Allows(OtherOpenProject, ProjectAction.Read).Should().BeFalse();
        scope.Allows(OpenProject, ProjectAction.Manage).Should().BeFalse("no project is open under the switch");
    }

    [Test]
    public async Task WithoutUserManagement_MembershipsAreUnavailable_AndRestrictedProjectsAreAdminOnly()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(User(), userManagement: false);

        scope.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse();
        scope.Allows(OpenProject, ProjectAction.Write).Should().BeTrue();
    }

    [Test]
    public async Task AHostMembershipSource_GrantsItsRoles()
    {
        var source = HostSource(new Dictionary<int, ProjectRole> { [RestrictedProject] = ProjectRole.Editor });

        var scope = await Evaluate(User(), membershipSource: source.Object);

        scope.Allows(RestrictedProject, ProjectAction.Write).Should().BeTrue();
    }

    [Test]
    public async Task AHostMembershipSource_ThatAnswersNothingOrFails_DeniesRestrictedProjects_AndNeverOpensOne()
    {
        var silent = HostSource(null);
        var failing = new Mock<IProjectMembershipSource>();
        failing.SetupGet(x => x.IsAvailable).Returns(true);
        failing
            .Setup(x => x.GetMembershipsAsync(It.IsAny<BeaconActor>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("directory unreachable"));

        var whenSilent = await Evaluate(User(), membershipSource: silent.Object);
        var whenFailing = await Evaluate(User(), membershipSource: failing.Object);

        whenSilent.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse();
        whenFailing.Allows(RestrictedProject, ProjectAction.Read).Should().BeFalse();
        whenFailing.Allows(OpenProject, ProjectAction.Read).Should().BeTrue();
    }

    [Test]
    public async Task ALegacyApiKey_ActsAsItsOwner_WithoutTheAdminBypass_AndNeverManagesMembers()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(Key(projects: null, isLiveAdmin: true));

        scope.IsUnrestricted.Should().BeFalse("a key never carries its owner's Admin bypass");
        scope.Allows(RestrictedProject, ProjectAction.Manage).Should().BeTrue();
        scope.Allows(RestrictedProject, ProjectAction.ManageMembers).Should().BeFalse();
        scope.For(ProjectAction.Read).IncludesOpen.Should().BeTrue("a key without a list reaches what its owner reaches");
    }

    [Test]
    public async Task AnApiKeyOfAnInactiveOwner_GetsNothing()
    {
        var scope = await Evaluate(Key(projects: null) with { IsActiveUser = false });

        scope.Should().BeSameAs(ProjectAccessScope.None);
    }

    [Test]
    public async Task AnApiKey_NeverExceedsItsOwner_AndAnOwnerWithoutARoleGivesItNothing()
    {
        var readerOwner = await Evaluate(Key(projects: null), ownerCeiling: ProjectRole.Viewer);
        var ownerWithoutRole = await Evaluate(Key(projects: null), ownerCeiling: null);

        readerOwner.For(ProjectAction.Read).IncludesOpen.Should().BeTrue();
        readerOwner.For(ProjectAction.Write).Ids.Should().BeEmpty("an Execute key of a reader writes nowhere");
        readerOwner.For(ProjectAction.Write).IncludesOpen.Should().BeFalse();
        readerOwner.Allows(OpenProject, ProjectAction.Query).Should().BeFalse();
        ownerWithoutRole.Should().BeSameAs(ProjectAccessScope.None);
    }

    [Test]
    public async Task TheRequestsOwnApiKey_IsCappedByWhatTheProviderLetsItsOwnerDo()
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(Key(projects: null));
        var reader = Build(accessor.Object, Ceiling.Viewer, requireMembership: false, userManagement: true, membershipSource: null, ownerCeiling: ProjectRole.Editor);
        var nobody = Build(accessor.Object, Ceiling.None, requireMembership: false, userManagement: true, membershipSource: null, ownerCeiling: ProjectRole.Editor);

        var readerScope = await reader.GetScopeAsync(CancellationToken.None);
        var nobodyScope = await nobody.GetScopeAsync(CancellationToken.None);

        readerScope.Allows(OpenProject, ProjectAction.Read).Should().BeTrue();
        readerScope.Allows(OpenProject, ProjectAction.Write).Should().BeFalse();
        nobodyScope.Should().BeSameAs(ProjectAccessScope.None);
    }

    [Test]
    public async Task AnApiKeyWithAList_IsConfinedToItsOwnersProjectsInTheList()
    {
        var scope = await Evaluate(Key(projects: [OpenProject, RestrictedProject, 99]));

        scope.For(ProjectAction.Read).Ids.Should().Equal(OpenProject);
        scope.For(ProjectAction.Read).IncludesOpen.Should().BeFalse();
        scope.Allows(OtherOpenProject, ProjectAction.Read).Should().BeFalse();
    }

    [Test]
    public async Task AReadScopedKey_OnlyReads()
    {
        var scope = await Evaluate(Key(projects: [OpenProject], keyScope: McpCallerScope.Read));

        scope.Allows(OpenProject, ProjectAction.Read).Should().BeTrue();
        scope.Allows(OpenProject, ProjectAction.Run).Should().BeFalse();
        scope.Allows(OpenProject, ProjectAction.Write).Should().BeFalse();
    }

    [Test]
    public async Task AnMcpSystemCaller_GetsItsConfiguredProjects_RestrictedOrNot_ForReadRunAndQuery()
    {
        var scope = await Evaluate(McpCaller(McpCallerKind.System, beaconUserId: null, [OpenProject, RestrictedProject]));

        scope.For(ProjectAction.Query).Ids.Should().Equal(OpenProject, RestrictedProject);
        scope.For(ProjectAction.Write).Ids.Should().BeEmpty();
        scope.For(ProjectAction.Read).IncludesOpen.Should().BeFalse();
    }

    [Test]
    public async Task AnMcpUserCaller_GetsItsConfiguredProjectsWithinItsUsersAccess()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.Viewer));

        var scope = await Evaluate(
            McpCaller(McpCallerKind.User, MemberUserId, [OpenProject, RestrictedProject]),
            ownerCeiling: ProjectRole.Editor);

        scope.Allows(OpenProject, ProjectAction.Query).Should().BeTrue();
        scope.Allows(RestrictedProject, ProjectAction.Read).Should().BeTrue();
        scope.Allows(RestrictedProject, ProjectAction.Query).Should().BeFalse("its membership is Viewer");
        scope.Allows(OtherOpenProject, ProjectAction.Read).Should().BeFalse("not configured for the caller");
    }

    [Test]
    public async Task AnMcpUserCallerWithoutAUser_GetsItsConfiguredOpenProjectsOnly()
    {
        var scope = await Evaluate(McpCaller(McpCallerKind.User, beaconUserId: null, [OpenProject, RestrictedProject]));

        scope.For(ProjectAction.Read).Ids.Should().Equal(OpenProject);
        scope.Allows(OpenProject, ProjectAction.Write).Should().BeFalse();
    }

    [Test]
    public async Task AnMcpUserCaller_IsCappedByTheLowerOfItsUsersRoleAndItsScope()
    {
        var readerUser = await Evaluate(McpCaller(McpCallerKind.User, MemberUserId, [OpenProject]), ownerCeiling: ProjectRole.Viewer);
        var readScope = await Evaluate(
            McpCaller(McpCallerKind.User, MemberUserId, [OpenProject]) with { ScopeCeiling = McpCallerScope.Read },
            ownerCeiling: ProjectRole.Editor);

        readerUser.Allows(OpenProject, ProjectAction.Read).Should().BeTrue();
        readerUser.Allows(OpenProject, ProjectAction.Query).Should().BeFalse("its user only reads");
        readScope.Allows(OpenProject, ProjectAction.Read).Should().BeTrue();
        readScope.Allows(OpenProject, ProjectAction.Run).Should().BeFalse("its scope only reads");
        readScope.Allows(OpenProject, ProjectAction.Query).Should().BeFalse();
    }

    [Test]
    public async Task AnMcpCallerWithoutAKind_GetsNothing()
    {
        var scope = await Evaluate(McpCaller(McpCallerKind.System, beaconUserId: null, [OpenProject]) with { CallerKind = null });

        scope.Should().BeSameAs(ProjectAccessScope.None);
    }

    [Test]
    public async Task AnUnmappedMcpCaller_GetsNothing()
    {
        var unmapped = McpCaller(McpCallerKind.User, null, [OpenProject]) with { ScopeCeiling = null };

        var scope = await Evaluate(unmapped);

        scope.Should().BeSameAs(ProjectAccessScope.None);
    }

    [Test]
    public async Task AnOwnerlessScheduledRow_ReachesOnlyTheOpenlyReachableSources()
    {
        var scope = await Evaluate(BeaconActor.Nobody with { Kind = BeaconPrincipalKind.Owner });

        scope.For(ProjectAction.Run).Ids.Should().BeEmpty();
        scope.For(ProjectAction.Run).IncludesOpen.Should().BeTrue();
        scope.For(ProjectAction.Run).NotOpenDataSourceIds.Should().Equal(20, 30, 50);
    }

    [Test]
    public async Task AnOwnerlessScheduledRow_ReachesNothing_WhenMembershipIsRequiredEverywhere()
    {
        var scope = await Evaluate(BeaconActor.Nobody with { Kind = BeaconPrincipalKind.Owner }, requireMembership: true);

        scope.For(ProjectAction.Run).IncludesOpen.Should().BeFalse();
        scope.For(ProjectAction.Run).Ids.Should().BeEmpty();
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task AnOwnerWithoutAStoredUser_IsTheSystemOnlyWithoutUserManagement(bool userManagement, bool unrestricted)
    {
        var scope = await Evaluate(new BeaconActor("host-owner", false) { Kind = BeaconPrincipalKind.Owner }, userManagement: userManagement);

        scope.IsUnrestricted.Should().Be(unrestricted);
        if (!unrestricted)
        {
            scope.Should().BeSameAs(ProjectAccessScope.None);
        }
    }

    [Test]
    public async Task AnInactiveOwner_GetsNothing_AndAnAdminOwnerEverything()
    {
        var inactive = Owner(isActive: false);
        var admin = Owner(isLiveAdmin: true);

        (await Evaluate(inactive)).Should().BeSameAs(ProjectAccessScope.None);
        (await Evaluate(admin)).IsUnrestricted.Should().BeTrue();
    }

    [Test]
    public async Task AnOwner_ActsWithItsMemberships_CappedByItsCeiling_WithoutManagingMembers()
    {
        _members.Add(Member(RestrictedProject, ProjectRole.ProjectAdmin));

        var scope = await Evaluate(Owner(), ownerCeiling: ProjectRole.Editor);

        scope.Allows(RestrictedProject, ProjectAction.Run).Should().BeTrue();
        scope.Allows(RestrictedProject, ProjectAction.ManageMembers).Should().BeFalse();
    }

    [Test]
    public async Task TheScope_IsReadOncePerRequest()
    {
        var actor = User();
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(actor);
        var evaluator = Build(accessor.Object, Ceiling.Editor, requireMembership: false, userManagement: true, membershipSource: null, ownerCeiling: null);

        var first = await evaluator.GetScopeAsync(CancellationToken.None);
        var second = await evaluator.GetScopeAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        _contextsCreated.Should().Be(1, "one snapshot command per request, memoized for the scope");
    }

    [Test]
    public async Task TheSnapshot_IsOneDatabaseCommand()
    {
        var capture = new SqlCapture().ThenNoRows();
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(User());
        var evaluator = new ProjectAccessEvaluator(
            accessor.Object,
            capture.Factory(),
            new DatabaseProjectMembershipSource(configuration, capture.Factory()),
            Mock.Of<IRunAsCeilingSource>(),
            Mock.Of<IServiceProvider>(),
            new BeaconConfiguration(),
            Options.Create(new ProjectAuthorizationOptions()),
            NullLogger<ProjectAccessEvaluator>.Instance,
            new OwnerRunsAsSystemLog(TimeProvider.System, NullLogger<OwnerRunsAsSystemLog>.Instance));

        await evaluator.GetScopeAsync(CancellationToken.None);
        await evaluator.GetScopeAsync(CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should().Contain("UNION ALL");
        capture.CommandParameters[0].Values.Should().Contain(MemberUserId, "the caller's membership rows come with the same command");
    }

    [Test]
    public void RequiringMembershipEverywhere_WithoutMemberships_FailsAtStartup()
    {
        var configuration = new BeaconConfiguration();
        var services = new ServiceCollection();
        services.AddSingleton<IProjectMembershipSource>(
            new DatabaseProjectMembershipSource(configuration, Mock.Of<IDbContextFactory<BeaconContext>>()));
        using var provider = services.BuildServiceProvider();
        var validator = new ProjectAuthorizationOptionsValidator(provider.GetRequiredService<IServiceScopeFactory>());

        var refused = validator.Validate(null, new ProjectAuthorizationOptions { RequireProjectMembership = true });
        var allowed = validator.Validate(null, new ProjectAuthorizationOptions { RequireProjectMembership = false });
        configuration.UserManagement.Enabled = true;
        var withUsers = validator.Validate(null, new ProjectAuthorizationOptions { RequireProjectMembership = true });

        refused.Failed.Should().BeTrue();
        refused.FailureMessage.Should().Contain("RequireProjectMembership");
        allowed.Succeeded.Should().BeTrue();
        withUsers.Succeeded.Should().BeTrue();
    }

    private async Task<ProjectAccessScope> Evaluate(
        BeaconActor actor,
        Ceiling ceiling = Ceiling.Editor,
        bool requireMembership = false,
        bool userManagement = true,
        IProjectMembershipSource? membershipSource = null,
        ProjectRole? ownerCeiling = ProjectRole.Editor)
    {
        var accessor = new Mock<IBeaconActorAccessor>();
        accessor.Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>())).ReturnsAsync(actor);
        var evaluator = Build(accessor.Object, ceiling, requireMembership, userManagement, membershipSource, ownerCeiling);

        return actor.Kind is BeaconPrincipalKind.Owner or BeaconPrincipalKind.McpCaller or BeaconPrincipalKind.ApiKey
            ? await evaluator.GetScopeAsync(actor, CancellationToken.None)
            : await evaluator.GetScopeAsync(CancellationToken.None);
    }

    private ProjectAccessEvaluator Build(
        IBeaconActorAccessor accessor,
        Ceiling ceiling,
        bool requireMembership,
        bool userManagement,
        IProjectMembershipSource? membershipSource,
        ProjectRole? ownerCeiling)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = userManagement;
        configuration.Authorization.Enabled = true;

        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                _contextsCreated++;

                return new RecordingBeaconContext(
                    new Dictionary<Type, object>
                    {
                        [typeof(Project)] = RecordingBeaconContext.MemorySet(_projects, []),
                        [typeof(DataSource)] = RecordingBeaconContext.MemorySet(_dataSources, []),
                        [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(_links, []),
                        [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(_members, [])
                    },
                    []);
            });

        var provider = new Mock<IBeaconAuthorizationProvider>();
        provider.Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ceiling == Ceiling.Editor);
        provider.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(ceiling != Ceiling.None);
        var services = new Mock<IServiceProvider>();
        services.Setup(x => x.GetService(typeof(IBeaconAuthorizationProvider))).Returns(provider.Object);

        var ceilingSource = new Mock<IRunAsCeilingSource>();
        ceilingSource
            .Setup(x => x.GetCeilingAsync(It.IsAny<BeaconActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ownerCeiling);

        return new ProjectAccessEvaluator(
            accessor,
            factory.Object,
            membershipSource ?? new DatabaseProjectMembershipSource(configuration, factory.Object),
            ceilingSource.Object,
            services.Object,
            configuration,
            Options.Create(new ProjectAuthorizationOptions { RequireProjectMembership = requireMembership }),
            NullLogger<ProjectAccessEvaluator>.Instance,
            new OwnerRunsAsSystemLog(TimeProvider.System, NullLogger<OwnerRunsAsSystemLog>.Instance));
    }

    private static BeaconActor User(bool isLiveAdmin = false, bool holdsAdminClaim = false)
    {
        return new BeaconActor("ext-member", holdsAdminClaim)
        {
            Kind = BeaconPrincipalKind.User,
            BeaconUserId = MemberUserId,
            IsActiveUser = true,
            IsLiveAdmin = isLiveAdmin,
            RoleLevel = isLiveAdmin ? RoleService.RoleLevels.Admin : RoleService.RoleLevels.Editor
        };
    }

    private static BeaconActor Key(IReadOnlyList<int>? projects, bool isLiveAdmin = false, McpCallerScope keyScope = McpCallerScope.Execute)
    {
        return new BeaconActor("ext-member", false)
        {
            Kind = BeaconPrincipalKind.ApiKey,
            IsApiKey = true,
            BeaconUserId = MemberUserId,
            IsActiveUser = true,
            IsLiveAdmin = isLiveAdmin,
            AllowedProjectIds = projects,
            ScopeCeiling = keyScope
        };
    }

    private static BeaconActor McpCaller(McpCallerKind kind, int? beaconUserId, IReadOnlyList<int> configured)
    {
        return new BeaconActor(beaconUserId == null ? null : "ext-member", false)
        {
            Kind = BeaconPrincipalKind.McpCaller,
            BeaconUserId = beaconUserId,
            IsActiveUser = beaconUserId != null,
            AllowedProjectIds = configured,
            ScopeCeiling = McpCallerScope.Execute,
            CallerKind = kind
        };
    }

    private static BeaconActor Owner(bool isActive = true, bool isLiveAdmin = false)
    {
        return new BeaconActor("ext-member", false)
        {
            Kind = BeaconPrincipalKind.Owner,
            BeaconUserId = MemberUserId,
            IsActiveUser = isActive,
            IsLiveAdmin = isLiveAdmin
        };
    }

    private static Mock<IProjectMembershipSource> HostSource(IReadOnlyDictionary<int, ProjectRole>? memberships)
    {
        var source = new Mock<IProjectMembershipSource>();
        source.SetupGet(x => x.IsAvailable).Returns(true);
        source
            .Setup(x => x.GetMembershipsAsync(It.IsAny<BeaconActor>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(memberships);

        return source;
    }

    private static ProjectMember Member(int projectId, ProjectRole role)
    {
        return new ProjectMember { ProjectId = projectId, UserId = MemberUserId, Role = role };
    }

    private ProjectDataSource Link(int projectId, int dataSourceId)
    {
        return new ProjectDataSource
        {
            ProjectId = projectId,
            DataSourceId = dataSourceId,
            Project = _projects.First(x => x.Id == projectId)
        };
    }

    private static DataSource Source(int id, bool openWhenUnlinked = false)
    {
        return new DataSource
        {
            Id = id,
            Name = $"source {id}",
            DataSourceType = DataSourceType.Database,
            EncryptedConnectionData = "enc",
            OpenWhenUnlinked = openWhenUnlinked
        };
    }

    public enum Ceiling
    {
        None,
        Viewer,
        Editor
    }
}
