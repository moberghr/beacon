using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
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
/// signed-in user holding the Admin role claim is an Admin.
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

        actor.Should().Be(BeaconActor.Nobody);
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
        var accessor = new BeaconActorAccessor(Accessor(Interactive("ext-ana")), capture.Factory());

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

    private Task<BeaconActor> Resolve(ClaimsPrincipal principal)
    {
        return BeaconActorAccessor.ResolveAsync(principal, Factory(), CancellationToken.None);
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
