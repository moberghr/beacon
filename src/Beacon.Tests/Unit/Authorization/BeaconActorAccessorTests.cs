using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// The caller as the owner, creator and assignee of resources: an API key is its owner by Users.Id, every other
/// principal is the user its NameIdentifier names by Users.ExternalId; a session without a stored user keeps its own
/// id; nobody is resolved for a request that names no one, without reading the database; and only an enabled,
/// signed-in user holding the Admin role claim is an Admin — while Beacon manages users, only one whose stored roles make
/// it one, whatever role claims a host's sign-in added.
/// </summary>
[TestFixture]
public class BeaconActorAccessorTests
{
    private List<BeaconUser> _users = null!;

    [SetUp]
    public void SetUp()
    {
        _users =
        [
            User(1, "ext-ana", "ana"),
            User(2, "ext-ben", "ben"),
            User(3, "ext-cid", "cid", isEnabled: false),
            User(4, "ext-dee", "dee", archived: true),
            User(5, "ext-admin", "admin")
        ];
    }

    [TestCaseSource(nameof(RequestsNamingNobody))]
    public async Task ARequestThatNamesNobody_HasNoUser_AndReadsNoUserRecord(ClaimsPrincipal? principal)
    {
        // A strict factory fails the test if a context is ever created.
        var factory = new Mock<IDbContextFactory<BeaconContext>>(MockBehavior.Strict);

        var actor = await BeaconActorAccessor.ResolveAsync(principal, factory.Object, CancellationToken.None);

        actor.UserId.Should().BeNull();
        actor.IsAdmin.Should().BeFalse();
        actor.Is(null).Should().BeFalse("a missing id never matches a missing owner or assignee");
    }

    [TestCaseSource(nameof(RequestsNamingNobody))]
    public async Task ARequestThatNamesNobody_IsNotMatchedToAStoredEnabledUser(ClaimsPrincipal? principal)
    {
        // ana is stored and enabled, and several of these requests carry her name or external id.
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, []) },
                []));

        var actor = await BeaconActorAccessor.ResolveAsync(principal, factory.Object, CancellationToken.None);

        actor.UserId.Should().BeNull();
        actor.IsAdmin.Should().BeFalse();
        actor.BeaconUserId.Should().BeNull("no stored user is matched");
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AnApiKey_IsItsOwnerByUsersId_NotByItsUserNameClaim()
    {
        // The key's user name claim names ben, its owner id names ana: the id decides.
        var actor = await Resolve(ApiKey(nameIdentifier: "1", userName: "ben"));

        actor.UserId.Should().Be("ext-ana");
    }

    [Test]
    public async Task AnApiKey_IsNeverAnAdmin_EvenWhenItsOwnerIsOne()
    {
        var actor = await Resolve(ApiKey(nameIdentifier: "5", userName: "admin"));

        actor.UserId.Should().Be("ext-admin");
        actor.IsAdmin.Should().BeFalse("an API key carries no role claim");
    }

    [Test]
    public async Task AnApiKey_CarriesItsKeyId_AndIsLoggedByItsOwnersUsersId()
    {
        var principal = ApiKey(nameIdentifier: "1", userName: "ana");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(McpCallerClaimTypes.ApiKeyId, "41"));

        var actor = await Resolve(principal);

        actor.ApiKeyId.Should().Be(41);
        actor.LogId.Should().Be("1", "access logs name a stored user by its Users.Id");
    }

    [Test]
    public void ACallerWithoutAStoredUser_IsLoggedByTheIdItsSessionPresents()
    {
        var hostAdmin = new BeaconActor("host-admin", true) { Kind = BeaconPrincipalKind.Session };

        hostAdmin.LogId.Should().Be("host-admin");
        BeaconActor.Nobody.LogId.Should().BeNull();
    }

    // The stamp Beacon's sign-in records in a cookie session: a session whose stamp the user no longer has (every session
    // signed out, a password reset) acts as nobody at once, before its next revalidation signs it out.
    [TestCase("stamp-1", "stamp-1", true)]
    [TestCase("stamp-1", "stamp-2", false)]
    [TestCase("", null, true)]
    [TestCase("", "stamp-2", false)]
    public async Task ASessionsStamp_MustBeTheStoredUsersStamp(string sessionStamp, string? storedStamp, bool isTheUser)
    {
        _users[0].SecurityStamp = storedStamp;
        var principal = Interactive("ext-ana");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(BeaconClaims.SessionStamp, sessionStamp, ClaimValueTypes.String, BeaconClaims.Issuer));

        var actor = await Resolve(principal);

        if (isTheUser)
        {
            actor.BeaconUserId.Should().Be(1);
            return;
        }

        actor.Should().Be(BeaconActor.Nobody);
    }

    [Test]
    public async Task AStampClaimFromAnotherIssuer_OrASessionWithoutOne_IsLeftToTheRevalidation()
    {
        _users[0].SecurityStamp = "stamp-2";
        var foreign = Interactive("ext-ana");
        ((ClaimsIdentity)foreign.Identity!).AddClaim(new Claim(BeaconClaims.SessionStamp, "stamp-1", ClaimValueTypes.String, "https://idp.example.test"));

        var withForeign = await Resolve(foreign);
        var without = await Resolve(Interactive("ext-ana"));

        withForeign.BeaconUserId.Should().Be(1);
        without.BeaconUserId.Should().Be(1);
    }

    [Test]
    public async Task AnApiKey_IsNeverJudgedByAStampClaim()
    {
        _users[0].SecurityStamp = "stamp-2";
        var principal = ApiKey(nameIdentifier: "1", userName: "ana");
        ((ClaimsIdentity)principal.Identity!).AddClaim(new Claim(BeaconClaims.SessionStamp, "stamp-1", ClaimValueTypes.String, BeaconClaims.Issuer));

        var actor = await Resolve(principal);

        actor.BeaconUserId.Should().Be(1, "a key is checked by its own revocation, not a session stamp");
    }

    [Test]
    public async Task AnApiKeyOfAnUnknownOwner_IsNobody()
    {
        var actor = await Resolve(ApiKey(nameIdentifier: "42", userName: "ana"));

        actor.Should().Be(BeaconActor.Nobody);
    }

    [Test]
    public async Task ABearerPrincipal_IsTheUserItsSubjectNames()
    {
        var bearer = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ext-ben"), new Claim(ClaimTypes.Role, RoleService.RoleNames.Editor)],
            "Bearer"));

        var actor = await Resolve(bearer);

        actor.UserId.Should().Be("ext-ben");
        actor.IsAdmin.Should().BeFalse();
    }

    [Test]
    public async Task ASessionWithoutAStoredUser_KeepsItsNameIdentifier_AndItsAdminClaim()
    {
        var actor = await Resolve(Interactive("ext-host-only", RoleService.RoleNames.Admin));

        actor.UserId.Should().Be("ext-host-only");
        actor.IsAdmin.Should().BeTrue();
    }

    [TestCase("ext-cid")]
    [TestCase("ext-dee")]
    public async Task ADisabledOrArchivedUser_HasNoUser_AndIsNoAdmin_WhateverItsClaims(string externalId)
    {
        var actor = await Resolve(Interactive(externalId, RoleService.RoleNames.Admin));

        actor.Should().Be(BeaconActor.Nobody);
    }

    [Test]
    public async Task AnEnabledUserWithTheAdminRoleClaim_IsAnAdmin()
    {
        var actor = await Resolve(Interactive("ext-admin", RoleService.RoleNames.Admin));

        actor.UserId.Should().Be("ext-admin");
        actor.IsAdmin.Should().BeTrue();
    }

    [Test]
    public async Task TheBeaconRoleClaimAlone_DoesNotMakeAnAdmin()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ext-admin"), new Claim(BeaconClaims.Role, RoleService.RoleNames.Admin)],
            "Cookies"));

        var actor = await Resolve(principal);

        actor.IsAdmin.Should().BeFalse("the Admin checks read the ClaimTypes.Role claim, as the BeaconApiAdmin policy does");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task AnActiveRecord_WinsOverAnArchivedOneWithTheSameExternalId(bool archivedFirst)
    {
        var archived = User(10, "ext-eve", "eve-old", archived: true);
        var active = User(11, "ext-eve", "eve");
        _users.AddRange(archivedFirst ? [archived, active] : [active, archived]);

        var actor = await Resolve(Interactive("ext-eve", RoleService.RoleNames.Admin));

        actor.UserId.Should().Be("ext-eve");
        actor.IsAdmin.Should().BeTrue();
    }

    [Test]
    public async Task TheSessionsOwnIds_NameTheCallerForLegacyAssignees_ButAnApiKeysDoNot()
    {
        var session = await Resolve(Interactive("ext-ana", beaconUserId: "ana"));
        var key = await Resolve(ApiKey(nameIdentifier: "1", userName: "ana"));

        session.IsAssignee("ana").Should().BeTrue();
        session.IsAssignee("ext-ana").Should().BeTrue();
        session.IsAssignee("ben").Should().BeFalse();
        key.IsAssignee("ext-ana").Should().BeTrue();
        key.IsAssignee("1").Should().BeFalse();
    }

    [Test]
    public async Task TheAccessor_ResolvesTheCallerOncePerScope()
    {
        var capture = new SqlCapture().ThenNoRows();
        var accessor = TestProjectAccess.ActorAccessor(TestRequestPrincipal.For(Interactive("ext-ana")), capture.Factory());

        var first = await accessor.GetCurrentAsync(CancellationToken.None);
        var second = await accessor.GetCurrentAsync(CancellationToken.None);

        second.Should().BeSameAs(first);
        capture.Commands.Should().ContainSingle();
    }

    [Test]
    public async Task ApiKeyLookup_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        await BeaconActorAccessor.ResolveAsync(ApiKey(nameIdentifier: "7", userName: "ana"), capture.Factory(), CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        where.Should().Contain("u.id = @").And.NotContain("user_name").And.NotContain("archived_time IS NULL",
            "archived users are read so their sessions never fall back to their claim");
        sql.Should().Contain("ORDER BY u.archived_time IS NOT NULL, u.is_enabled DESC, u.id");
        capture.CommandParameters[0].Values.Should().Contain(7);
    }

    [Test]
    public async Task SessionLookup_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        await BeaconActorAccessor.ResolveAsync(Interactive("ext-ana"), capture.Factory(), CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("u.external_id = @");
        capture.CommandParameters[0].Values.Should().Contain("ext-ana");
    }

    [Test]
    public async Task TheAuditActor_OfAnApiKey_IsItsOwnersUsersId_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();
        var resolver = new ActorUserResolver(Accessor(ApiKey(nameIdentifier: "7", userName: "ana")), capture.Factory());

        await resolver.ResolveActorUserIdAsync(CancellationToken.None);

        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("u.id = @").And.NotContain("external_id = @");
        capture.CommandParameters[0].Values.Should().Contain(7);
    }

    [Test]
    public async Task TheAuditActor_OfASession_IsFoundByExternalId()
    {
        var resolver = new ActorUserResolver(Accessor(Interactive("ext-ben")), Factory());

        var userId = await resolver.ResolveActorUserIdAsync(CancellationToken.None);

        userId.Should().Be(2);
    }

    [Test]
    public async Task TheAuditActor_OfAnApiKey_IsFoundByItsOwnerId()
    {
        // A user whose ExternalId is the key's numeric id must not be taken for the owner.
        _users.Add(User(6, "2", "someone-else"));
        var resolver = new ActorUserResolver(Accessor(ApiKey(nameIdentifier: "2", userName: "ben")), Factory());

        var userId = await resolver.ResolveActorUserIdAsync(CancellationToken.None);

        userId.Should().Be(2);
    }

    private static IEnumerable<TestCaseData> RequestsNamingNobody()
    {
        yield return new TestCaseData(null).SetName("{m}(no principal)");
        yield return new TestCaseData(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ana")])))
            .SetName("{m}(unauthenticated)");
        yield return new TestCaseData(Interactive("")).SetName("{m}(empty NameIdentifier)");
        yield return new TestCaseData(ApiKey(nameIdentifier: null, userName: "ana")).SetName("{m}(API key without owner id)");
        yield return new TestCaseData(ApiKey(nameIdentifier: "", userName: "ana")).SetName("{m}(API key with an empty owner id)");
        yield return new TestCaseData(ApiKey(nameIdentifier: "ext-ana", userName: "ana")).SetName("{m}(API key with a non-numeric owner id)");
        yield return new TestCaseData(ApiKey(nameIdentifier: "0", userName: "ana")).SetName("{m}(API key with owner id zero)");
    }

    [Test]
    public async Task AMappedMcpCaller_IsItsProvisionedUserByUsersId_NotAnExternalIdLookup()
    {
        // A user whose ExternalId is the caller's numeric id must not be taken for it.
        _users.Add(User(6, "2", "someone-else"));

        var actor = await Resolve(McpCaller(nameIdentifier: "2", allowedProjects: "[4]"));

        actor.UserId.Should().Be("ext-ben");
        actor.Kind.Should().Be(BeaconPrincipalKind.McpCaller);
        actor.BeaconUserId.Should().Be(2);
        actor.CallerKind.Should().Be(McpCallerKind.User);
        actor.ScopeCeiling.Should().Be(McpCallerScope.Execute);
        actor.AllowedProjectIds.Should().Equal(4);
        actor.SessionIds.Should().BeEmpty("an MCP caller never names a legacy assignee by its session ids");
    }

    [Test]
    public async Task AMappedMcpCallerWithoutAProvisionedUser_ActsWithoutOne()
    {
        var actor = await Resolve(McpCaller(nameIdentifier: "f00dfeed", allowedProjects: "[4]"));

        actor.UserId.Should().BeNull();
        actor.BeaconUserId.Should().BeNull();
        actor.Kind.Should().Be(BeaconPrincipalKind.McpCaller);
        actor.AllowedProjectIds.Should().Equal(4);
    }

    [Test]
    public async Task AStoredUser_CarriesItsLiveAdminStandingAndRoleLevel_WhateverItsClaims()
    {
        _users[0].UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = RoleService.RoleNames.Admin, Level = RoleService.RoleLevels.Admin } });
        _users[1].UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = RoleService.RoleNames.Viewer, Level = RoleService.RoleLevels.Viewer } });

        var promoted = await Resolve(Interactive("ext-ana"));
        var demoted = await Resolve(Interactive("ext-ben", RoleService.RoleNames.Admin));

        promoted.IsLiveAdmin.Should().BeTrue();
        promoted.IsAdmin.Should().BeFalse("the claim-based Admin check is unchanged");
        promoted.Kind.Should().Be(BeaconPrincipalKind.User);
        demoted.IsLiveAdmin.Should().BeFalse("a stale Admin claim is not a live Admin role");
        demoted.RoleLevel.Should().Be(RoleService.RoleLevels.Viewer);
    }

    [Test]
    public async Task WhileBeaconManagesUsers_AStoredUsersAdminStanding_IsItsLiveRoles_NotItsClaim()
    {
        _users[0].UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = RoleService.RoleNames.Admin, Level = RoleService.RoleLevels.Admin } });

        var liveAdminWithoutTheClaim = await ResolveManaged(Interactive("ext-ana"));
        var claimWithoutTheRole = await ResolveManaged(Interactive("ext-ben", RoleService.RoleNames.Admin));

        liveAdminWithoutTheClaim.IsAdmin.Should().BeTrue();
        claimWithoutTheRole.IsAdmin.Should().BeFalse("a host sign-in's Admin role claim makes no Beacon Admin while Beacon manages users");
    }

    [Test]
    public async Task WhileBeaconManagesUsers_AnApiKeyOrMappedCallerOfAnAdmin_IsNoAdmin()
    {
        _users[0].UserRoles.Add(new BeaconUserRole { Role = new BeaconRole { Name = RoleService.RoleNames.Admin, Level = RoleService.RoleLevels.Admin } });
        var mappedCaller = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod)],
            "Bearer"));

        var apiKey = await ResolveManaged(ApiKey(nameIdentifier: "1", userName: "ana"));
        var mapped = await ResolveManaged(mappedCaller);

        apiKey.BeaconUserId.Should().Be(1);
        apiKey.IsAdmin.Should().BeFalse("an API key never carries the Admin bypass, even when its owner is an Admin");
        mapped.BeaconUserId.Should().Be(1);
        mapped.IsAdmin.Should().BeFalse("neither does a mapped MCP caller");
    }

    [Test]
    public async Task WhileBeaconManagesUsers_ASessionWithoutAStoredUser_IsNoAdmin_WhateverItsClaims()
    {
        var actor = await ResolveManaged(Interactive("ext-host-only", RoleService.RoleNames.Admin));

        actor.UserId.Should().Be("ext-host-only");
        actor.IsAdmin.Should().BeFalse();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task TheAccessor_ReadsWhetherBeaconManagesUsers_FromItsConfiguration(bool userManagement, bool isAdmin)
    {
        var configuration = new Beacon.Core.BeaconConfiguration { UserManagement = new Beacon.Core.UserManagementOptions { Enabled = userManagement } };
        var principal = TestRequestPrincipal.For(Interactive("ext-host-only", RoleService.RoleNames.Admin));
        var accessor = new BeaconActorAccessor(
            Factory(),
            new SystemWorkScope(),
            new RunAsScope(principal),
            new UnmarkedSystemWorkLog(new FakeTimeProvider(), new LogRecorder().For<UnmarkedSystemWorkLog>()),
            principal,
            configuration);

        var actor = await accessor.GetCurrentAsync(CancellationToken.None);

        actor.IsAdmin.Should().Be(isAdmin);
    }

    [Test]
    public async Task AnApiKey_CarriesItsScopeAndProjectList()
    {
        var actor = await Resolve(ApiKey(nameIdentifier: "1", userName: "ana"));

        actor.Kind.Should().Be(BeaconPrincipalKind.ApiKey);
        actor.BeaconUserId.Should().Be(1);
        actor.ScopeCeiling.Should().Be(McpCallerScope.Execute);
        actor.AllowedProjectIds.Should().BeNull("a key without a list carries no claim");
    }

    [Test]
    public async Task WorkWithoutARequest_InAnUnmarkedScope_IsNobody_AndOwnsNothing()
    {
        var accessor = TestProjectAccess.ActorAccessor(TestRequestPrincipal.OffRequest(), Factory());

        var actor = await accessor.GetCurrentAsync(CancellationToken.None);

        actor.Should().BeSameAs(BeaconActor.Nobody, "work without a request is the system only in a scope marked as system work");
        actor.Kind.Should().Be(BeaconPrincipalKind.Anonymous);
        actor.UserId.Should().BeNull();
        actor.IsAdmin.Should().BeFalse();
    }

    [Test]
    public async Task AnOwner_IsResolvedByExternalId_WithItsActiveState()
    {
        var accessor = TestProjectAccess.ActorAccessor(TestRequestPrincipal.OffRequest(), Factory());

        var active = await accessor.ResolveOwnerAsync("ext-ana", CancellationToken.None);
        var disabled = await accessor.ResolveOwnerAsync("ext-cid", CancellationToken.None);
        var unknown = await accessor.ResolveOwnerAsync("ext-gone", CancellationToken.None);
        var ownerless = await accessor.ResolveOwnerAsync(null, CancellationToken.None);

        active.Should().BeEquivalentTo(new { Kind = BeaconPrincipalKind.Owner, UserId = "ext-ana", BeaconUserId = (int?)1, IsActiveUser = true }, x => x.ExcludingMissingMembers());
        disabled.IsActiveUser.Should().BeFalse();
        disabled.BeaconUserId.Should().Be(3);
        unknown.BeaconUserId.Should().BeNull();
        unknown.UserId.Should().Be("ext-gone");
        ownerless.UserId.Should().BeNull();
        ownerless.Kind.Should().Be(BeaconPrincipalKind.Owner);
    }

    [TestCase("System", McpCallerKind.System)]
    [TestCase("User", McpCallerKind.User)]
    [TestCase("", null)]
    [TestCase("system", null)]
    [TestCase("99", null)]
    [TestCase(null, null)]
    public async Task AMappedMcpCallersKind_IsReadOnlyFromItsExactName(string? callerKind, McpCallerKind? expected)
    {
        var actor = await Resolve(McpCallerWith(callerKind, BeaconScopes.Execute, nameIdentifier: "f00dfeed"));

        actor.Kind.Should().Be(BeaconPrincipalKind.McpCaller);
        actor.CallerKind.Should().Be(expected);
    }

    [TestCase(BeaconScopes.Execute, McpCallerScope.Execute)]
    [TestCase(BeaconScopes.Read, McpCallerScope.Read)]
    [TestCase(null, null)]
    public async Task AScopedCallersScope_IsExecuteOrRead_OrNoneWithoutTheClaim(string? scope, McpCallerScope? expected)
    {
        var key = await Resolve(ApiKeyWith(nameIdentifier: "1", userName: "ana", scope));
        var caller = await Resolve(McpCallerWith(nameof(McpCallerKind.User), scope, nameIdentifier: "2"));

        key.Kind.Should().Be(BeaconPrincipalKind.ApiKey);
        key.ScopeCeiling.Should().Be(expected);
        caller.Kind.Should().Be(BeaconPrincipalKind.McpCaller);
        caller.ScopeCeiling.Should().Be(expected);
    }

    [TestCase("3")]
    [TestCase("4")]
    public async Task AScopedCallerOfADisabledOrArchivedUser_IsNobody(string userId)
    {
        var key = await Resolve(ApiKey(nameIdentifier: userId, userName: "cid"));
        var caller = await Resolve(McpCallerWith(nameof(McpCallerKind.User), BeaconScopes.Execute, nameIdentifier: userId));

        key.Should().BeSameAs(BeaconActor.Nobody);
        caller.Should().BeSameAs(BeaconActor.Nobody);
    }

    [Test]
    public async Task WorkWithoutARequest_InAScopeMarkedAsSystemWork_IsTheSystem_WithoutAWarning()
    {
        var logs = new LogRecorder();
        var systemWork = new SystemWorkScope();
        systemWork.MarkAsSystemWork();
        var accessor = new BeaconActorAccessor(
            Factory(),
            systemWork,
            new RunAsScope(TestRequestPrincipal.OffRequest()),
            new UnmarkedSystemWorkLog(new FakeTimeProvider(), logs.For<UnmarkedSystemWorkLog>()),
            TestRequestPrincipal.OffRequest());

        var actor = await accessor.GetCurrentAsync(CancellationToken.None);

        actor.Kind.Should().Be(BeaconPrincipalKind.System);
        logs.Entries.Should().BeEmpty();
    }

    [Test]
    public async Task WorkWithoutARequest_InAnUnmarkedScope_GetsNoAccess_AndWarnsAtMostOncePerInterval()
    {
        var logs = new LogRecorder();
        var time = new FakeTimeProvider();
        var unmarked = new UnmarkedSystemWorkLog(time, logs.For<UnmarkedSystemWorkLog>());

        var first = await new BeaconActorAccessor(Factory(), new SystemWorkScope(), new RunAsScope(TestRequestPrincipal.OffRequest()), unmarked, TestRequestPrincipal.OffRequest()).GetCurrentAsync(CancellationToken.None);
        await new BeaconActorAccessor(Factory(), new SystemWorkScope(), new RunAsScope(TestRequestPrincipal.OffRequest()), unmarked, TestRequestPrincipal.OffRequest()).GetCurrentAsync(CancellationToken.None);
        var afterBurst = logs.Entries.Count;
        time.Advance(UnmarkedSystemWorkLog.Interval);
        await new BeaconActorAccessor(Factory(), new SystemWorkScope(), new RunAsScope(TestRequestPrincipal.OffRequest()), unmarked, TestRequestPrincipal.OffRequest()).GetCurrentAsync(CancellationToken.None);

        first.Kind.Should().Be(BeaconPrincipalKind.Anonymous, "an unmarked scope without a request is nobody");
        afterBurst.Should().Be(1, "a second unmarked scope within the interval is not reported again");
        logs.Entries.Should().HaveCount(2);
        logs.Entries.Should().OnlyContain(x => x.Level == LogLevel.Warning && x.Message.Contains("not marked as system work"));
    }

    [Test]
    public void AnOwnerRunningAsTheSystem_IsWarnedAboutAtMostOncePerIntervalForEachOwner()
    {
        var logs = new LogRecorder();
        var time = new FakeTimeProvider();
        var log = new OwnerRunsAsSystemLog(time, logs.For<OwnerRunsAsSystemLog>());

        log.Report("host-principal");
        log.Report("host-principal");
        log.Report("other-principal");
        var afterBurst = logs.Entries.Count;
        time.Advance(OwnerRunsAsSystemLog.Interval);
        log.Report("host-principal");

        afterBurst.Should().Be(2, "one warning per owner within the interval");
        logs.Entries.Should().HaveCount(3);
        logs.Entries.Should().OnlyContain(x => x.Level == LogLevel.Warning && x.Message.Contains("runs as the system"));
        logs.Entries.Select(x => x.Message).Should().Contain(x => x.Contains("owner other-principal"));
    }

    [Test]
    public async Task ARequestsCaller_IsResolvedFromItsPrincipal_EvenInAScopeMarkedAsSystemWork()
    {
        var systemWork = new SystemWorkScope();
        systemWork.MarkAsSystemWork();
        var accessor = new BeaconActorAccessor(
            Factory(),
            systemWork,
            new RunAsScope(TestRequestPrincipal.OffRequest()),
            new UnmarkedSystemWorkLog(new FakeTimeProvider(), new LogRecorder().For<UnmarkedSystemWorkLog>()),
            TestRequestPrincipal.For(Interactive("ext-ana")));

        var actor = await accessor.GetCurrentAsync(CancellationToken.None);

        actor.Kind.Should().Be(BeaconPrincipalKind.User);
        actor.UserId.Should().Be("ext-ana");
    }

    [Test]
    public async Task WorkWithoutARequest_InAScopeRunningAsAnOwner_IsThatOwner()
    {
        var principal = TestRequestPrincipal.OffRequest();
        var runAs = new RunAsScope(principal);
        runAs.RunAs("ext-ana");
        var accessor = new BeaconActorAccessor(
            Factory(),
            new SystemWorkScope(),
            runAs,
            new UnmarkedSystemWorkLog(new FakeTimeProvider(), new LogRecorder().For<UnmarkedSystemWorkLog>()),
            principal);

        var actor = await accessor.GetCurrentAsync(CancellationToken.None);

        actor.Kind.Should().Be(BeaconPrincipalKind.Owner);
        actor.UserId.Should().Be("ext-ana");
        actor.BeaconUserId.Should().Be(1);
    }

    [Test]
    public void ARequestsScope_NeverRunsAsAnOwner()
    {
        var runAs = new RunAsScope(TestRequestPrincipal.For(Interactive("ext-ana")));

        var act = () => runAs.RunAs("ext-ben");

        act.Should().Throw<InvalidOperationException>().WithMessage("*never runs as the owner*");
        runAs.IsSet.Should().BeFalse();
    }

    [Test]
    public async Task ARequestWithoutAnAuthenticatedCaller_IsNotTheSystem_EvenInAMarkedScope()
    {
        var systemWork = new SystemWorkScope();
        systemWork.MarkAsSystemWork();
        var accessor = new BeaconActorAccessor(
            Factory(),
            systemWork,
            new RunAsScope(TestRequestPrincipal.For()),
            new UnmarkedSystemWorkLog(new FakeTimeProvider(), new LogRecorder().For<UnmarkedSystemWorkLog>()),
            TestRequestPrincipal.For());

        var actor = await accessor.GetCurrentAsync(CancellationToken.None);

        actor.Kind.Should().NotBe(BeaconPrincipalKind.System, "a request acts as its caller, never as the system");
        actor.BeaconUserId.Should().BeNull();
    }

    private Task<BeaconActor> Resolve(ClaimsPrincipal principal)
    {
        return BeaconActorAccessor.ResolveAsync(principal, Factory(), CancellationToken.None);
    }

    private Task<BeaconActor> ResolveManaged(ClaimsPrincipal principal)
    {
        return BeaconActorAccessor.ResolveAsync(principal, Factory(), CancellationToken.None, beaconManagesUsers: true);
    }

    private IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(_users, [])
                },
                []));

        return factory.Object;
    }

    private static BeaconUser User(int id, string externalId, string userName, bool isEnabled = true, bool archived = false)
    {
        return new BeaconUser
        {
            Id = id,
            ExternalId = externalId,
            UserName = userName,
            IsEnabled = isEnabled,
            ArchivedTime = archived ? DateTime.UtcNow : null
        };
    }

    private static HttpContextAccessor Accessor(ClaimsPrincipal user)
    {
        return new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
    }

    private static ClaimsPrincipal Interactive(string externalId, string? role = null, string? beaconUserId = null)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, externalId) };
        if (role != null)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        if (beaconUserId != null)
        {
            claims.Add(new Claim(BeaconClaims.UserId, beaconUserId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    private static ClaimsPrincipal McpCaller(string nameIdentifier, string allowedProjects)
    {
        var claims = new List<Claim>
        {
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod),
            new(McpCallerClaimTypes.Scope, "Execute"),
            new(McpCallerClaimTypes.CallerKind, nameof(McpCallerKind.User)),
            new(McpCallerClaimTypes.AllowedProjects, allowedProjects),
            new(ClaimTypes.NameIdentifier, nameIdentifier)
        };

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal McpCallerWith(string? callerKind, string? scope, string nameIdentifier)
    {
        var claims = new List<Claim>
        {
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod),
            new(McpCallerClaimTypes.AllowedProjects, "[4]"),
            new(ClaimTypes.NameIdentifier, nameIdentifier)
        };
        if (callerKind != null)
        {
            claims.Add(new Claim(McpCallerClaimTypes.CallerKind, callerKind));
        }

        if (scope != null)
        {
            claims.Add(new Claim(McpCallerClaimTypes.Scope, scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer"));
    }

    private static ClaimsPrincipal ApiKeyWith(string nameIdentifier, string userName, string? scope)
    {
        var claims = new List<Claim>
        {
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.UserNameClaim, userName),
            new(ClaimTypes.NameIdentifier, nameIdentifier)
        };
        if (scope != null)
        {
            claims.Add(new Claim(McpCallerClaimTypes.Scope, scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }

    private static ClaimsPrincipal ApiKey(string? nameIdentifier, string userName)
    {
        var claims = new List<Claim>
        {
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.Scope, "Execute"),
            new(McpCallerClaimTypes.UserNameClaim, userName)
        };
        if (nameIdentifier != null)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, nameIdentifier));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }
}
