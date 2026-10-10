using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.ApiKeys;
using Beacon.Core.Helpers;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// API keys follow their owner's lifecycle: disabling or archiving a user revokes every key of theirs in the same
/// save, re-enabling revokes whatever is still active instead of restoring anything, and the key of a disabled or
/// archived owner no longer validates. The listings call a key active by the same rule. Administrators list every key
/// and revoke any of them, from a signed-in session only; nobody else can. No database: EF async operators run against
/// mocked sets backed by the TestAsyncQueryable doubles (§4.7); the handlers' real SQL through SqlCapture.
/// </summary>
[TestFixture]
public class ApiKeyLifecycleTests
{
    private const string PlainTextKey = "sk-sem_LifecycleRegressionKeyMaterial000000";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private const string TestPrincipalHeader = "X-Test-Principal";
    private const string AntiforgeryCookieName = "beacon-test-antiforgery";

    [Test]
    public async Task DisablingAUser_RevokesAllTheirActiveKeys_InTheSameSave()
    {
        var store = new FakeStore();
        var user = store.AddUser(2);
        var other = store.AddUser(3);
        var first = store.AddKey(11, user);
        var second = store.AddKey(12, user);
        var earlier = DateTime.UtcNow.AddDays(-3);
        var alreadyRevoked = store.AddKey(13, user, revokedAt: earlier);
        var othersKey = store.AddKey(14, other);

        var result = await BuildUsers(store).ToggleUserEnabledAsync(2, CancellationToken.None);

        result.Success.Should().BeTrue();
        user.IsEnabled.Should().BeFalse();
        new[] { first, second }.Should().OnlyContain(x => x.IsRevoked && x.RevokedAt == Now.UtcDateTime);
        alreadyRevoked.RevokedAt.Should().Be(earlier, "an earlier revocation keeps its time");
        othersKey.IsRevoked.Should().BeFalse();
        store.Saves.Should().ContainSingle("the user and their keys change in one unit of work")
            .Which.Should().Be(new SaveSnapshot(UserEnabled: false, UserArchived: false, ActiveKeysOfUser: 0));
    }

    [Test]
    public async Task ReEnablingAUser_DoesNotBringTheirKeysBack()
    {
        var store = new FakeStore();
        var user = store.AddUser(2);
        var key = store.AddKey(11, user);
        var users = BuildUsers(store);

        await users.ToggleUserEnabledAsync(2, CancellationToken.None);
        await users.ToggleUserEnabledAsync(2, CancellationToken.None);

        user.IsEnabled.Should().BeTrue();
        key.IsRevoked.Should().BeTrue();
    }

    [Test]
    public async Task ReEnablingAUser_RevokesAKeyStillActive_InTheSameSave()
    {
        // A key issued while the user was being disabled, or kept by a user disabled before keys followed their owner.
        var store = new FakeStore();
        var user = store.AddUser(2, enabled: false);
        var key = store.AddKey(11, user);

        var result = await BuildUsers(store).ToggleUserEnabledAsync(2, CancellationToken.None);

        result.Success.Should().BeTrue();
        user.IsEnabled.Should().BeTrue();
        key.IsRevoked.Should().BeTrue();
        key.RevokedAt.Should().Be(Now.UtcDateTime);
        store.Saves.Should().ContainSingle()
            .Which.Should().Be(new SaveSnapshot(UserEnabled: true, UserArchived: false, ActiveKeysOfUser: 0));
    }

    [Test]
    public async Task UpdatingADisabledUserToEnabled_RevokesTheirStillActiveKeys()
    {
        var store = new FakeStore();
        var user = store.AddUser(2, enabled: false);
        var key = store.AddKey(11, user);

        await BuildUsers(store).UpdateUserAsync(Update(2, isEnabled: true), CancellationToken.None);

        user.IsEnabled.Should().BeTrue();
        key.IsRevoked.Should().BeTrue();
    }

    [Test]
    public async Task SavingADisabledUserAgain_RevokesAnyKeyStillActive()
    {
        var store = new FakeStore();
        var user = store.AddUser(2, enabled: false);
        var key = store.AddKey(11, user);

        await BuildUsers(store).UpdateUserAsync(Update(2, isEnabled: false), CancellationToken.None);

        key.IsRevoked.Should().BeTrue();
        store.Saves.Should().ContainSingle()
            .Which.Should().Be(new SaveSnapshot(UserEnabled: false, UserArchived: false, ActiveKeysOfUser: 0));
    }

    [Test]
    public async Task UpdatingAUserToDisabled_RevokesTheirKeys_InTheSameSave()
    {
        var store = new FakeStore();
        var user = store.AddUser(2);
        var key = store.AddKey(11, user);

        var result = await BuildUsers(store).UpdateUserAsync(Update(2, isEnabled: false), CancellationToken.None);

        result.Success.Should().BeTrue();
        key.IsRevoked.Should().BeTrue();
        store.Saves.Should().ContainSingle()
            .Which.Should().Be(new SaveSnapshot(UserEnabled: false, UserArchived: false, ActiveKeysOfUser: 0));
    }

    [Test]
    public async Task UpdatingAnEnabledUserWithoutDisablingThem_LeavesTheirKeysAlone()
    {
        var store = new FakeStore();
        var user = store.AddUser(2);
        var key = store.AddKey(11, user);

        await BuildUsers(store).UpdateUserAsync(Update(2, isEnabled: true), CancellationToken.None);

        key.IsRevoked.Should().BeFalse();
    }

    [Test]
    public async Task ArchivingAUser_RevokesTheirKeys_InTheSameSave()
    {
        var store = new FakeStore();
        var user = store.AddUser(2);
        var key = store.AddKey(11, user);

        var result = await BuildUsers(store).DeleteUserAsync(2, CancellationToken.None);

        result.Success.Should().BeTrue();
        key.IsRevoked.Should().BeTrue();
        store.Saves.Should().ContainSingle()
            .Which.Should().Be(new SaveSnapshot(UserEnabled: true, UserArchived: true, ActiveKeysOfUser: 0));
    }

    [Test]
    public async Task RefusingToDisableTheLastSuperAdmin_LeavesTheirKeysAlone()
    {
        var store = new FakeStore();
        var admin = store.AddUser(1, isSuperAdmin: true);
        var key = store.AddKey(11, admin);

        var result = await BuildUsers(store).ToggleUserEnabledAsync(1, CancellationToken.None);

        result.Success.Should().BeFalse();
        key.IsRevoked.Should().BeFalse();
        store.Saves.Should().BeEmpty();
    }

    [Test]
    public async Task KeyOfADisabledAndThenArchivedOwner_NoLongerValidates()
    {
        var store = new FakeStore();
        var user = store.AddUser(2);
        store.AddKey(11, user);
        var users = BuildUsers(store);
        var apiKeys = BuildApiKeys(store);

        var before = await apiKeys.ValidateApiKeyAsync(PlainTextKey, CancellationToken.None);
        await users.ToggleUserEnabledAsync(2, CancellationToken.None);
        await users.DeleteUserAsync(2, CancellationToken.None);
        var after = await apiKeys.ValidateApiKeyAsync(PlainTextKey, CancellationToken.None);

        before.Should().NotBeNull();
        after.Should().BeNull();
    }

    [Test]
    public async Task OwnerRevoke_StillOnlyRevokesTheCallersOwnKey()
    {
        var store = new FakeStore();
        var owner = store.AddUser(2);
        store.AddKey(21, owner);
        var apiKeys = new Mock<IApiKeyService>();
        var handler = new RevokeApiKeyHandler(apiKeys.Object, BuildFactory(store), Accessor(CookiePrincipal("admin")), AdminUsers().Object);

        var act = () => handler.Handle(new RevokeApiKeyCommand(21), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>("another user's key goes through the admin route");
        apiKeys.Verify(x => x.RevokeApiKeyAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task AdminRevoke_RevokesAnyUsersKey_AtTheClocksTime()
    {
        var store = new FakeStore();
        var owner = store.AddUser(2);
        var key = store.AddKey(21, owner);

        await AdminRevoke(store).Handle(new AdminRevokeApiKeyCommand(21), CancellationToken.None);

        key.IsRevoked.Should().BeTrue();
        key.RevokedAt.Should().Be(Now.UtcDateTime);
    }

    [Test]
    public async Task RevokingARevokedKey_KeepsTheFirstRevocationTime()
    {
        var store = new FakeStore();
        var owner = store.AddUser(2);
        var first = Now.UtcDateTime.AddDays(-2);
        var key = store.AddKey(21, owner, revokedAt: first);

        await AdminRevoke(store).Handle(new AdminRevokeApiKeyCommand(21), CancellationToken.None);

        key.RevokedAt.Should().Be(first);
        store.Saves.Should().BeEmpty("nothing changed");
    }

    [Test]
    public async Task AdminRevoke_LogsTheActingAdministratorAndTheKey_AndNoKeyMaterial()
    {
        var store = new FakeStore();
        var owner = store.AddUser(2);
        store.AddKey(21, owner);
        var logs = new LogRecorder();
        var handler = new AdminRevokeApiKeyHandler(BuildApiKeys(store), Accessor(CookiePrincipal("admin")), AdminUsers().Object, logs.For<AdminRevokeApiKeyHandler>());

        await handler.Handle(new AdminRevokeApiKeyCommand(21), CancellationToken.None);

        logs.Entries.Should().ContainSingle()
            .Which.Message.Should().Be("API key 21 revoked by administrator 1");
        logs.Contains("sk-sem_").Should().BeFalse();
    }

    [Test]
    public async Task AdminRevoke_OfAnUnknownKey_IsRefused()
    {
        var store = new FakeStore();

        var act = () => AdminRevoke(store).Handle(new AdminRevokeApiKeyCommand(99), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    [TestCase("api_key")]
    [TestCase("jwt")]
    public async Task AdminHandlers_RefuseACallerCarryingAnAuthMethod_EvenWithTheAdminRole(string authMethod)
    {
        var store = new FakeStore();
        var owner = store.AddUser(2);
        var key = store.AddKey(21, owner);
        var principal = CookiePrincipal("admin", new Claim(McpCallerClaimTypes.AuthMethod, authMethod));

        var revoke = () => new AdminRevokeApiKeyHandler(BuildApiKeys(store), Accessor(principal), AdminUsers().Object, NullLogger<AdminRevokeApiKeyHandler>.Instance)
            .Handle(new AdminRevokeApiKeyCommand(21), CancellationToken.None);
        var list = () => new GetAllApiKeysHandler(BuildFactory(store), Accessor(principal), Options.Create(new ApiKeyOptions()), new FakeTimeProvider(Now))
            .Handle(new GetAllApiKeysQuery(), CancellationToken.None);

        await revoke.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*signed-in session*");
        await list.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*signed-in session*");
        key.IsRevoked.Should().BeFalse();
    }

    [Test]
    public async Task AdminList_ShowsEveryKeyWithItsOwner_ScopesAndProjects_AndFiltersByUser()
    {
        var store = new FakeStore();
        var ada = store.AddUser(2, userName: "ada");
        var bob = store.AddUser(3, userName: "bob");
        var adaKey = store.AddKey(11, ada, scopes: "[\"Read\",\"Admin\"]", projects: "[4,5]");
        store.AddKey(12, bob, revokedAt: Now.UtcDateTime);
        var handler = AdminList(store);

        var all = await handler.Handle(new GetAllApiKeysQuery(), CancellationToken.None);
        var onlyAda = await handler.Handle(new GetAllApiKeysQuery { UserId = 2 }, CancellationToken.None);

        all.TotalCount.Should().Be(2);
        all.Items.Select(x => x.UserName).Should().BeEquivalentTo("ada", "bob");
        all.Items.Single(x => x.Id == 12).IsActive.Should().BeFalse();
        var entry = onlyAda.Items.Should().ContainSingle().Subject;
        entry.Id.Should().Be(adaKey.Id);
        entry.UserId.Should().Be(2);
        entry.Scopes.Should().Equal("Read", "Execute");
        entry.AllowedProjectIds.Should().Equal(4, 5);
        entry.IsActive.Should().BeTrue();
    }

    [Test]
    public async Task AdminList_ListsAnArchivedOwnersKeyUnderTheirName_AsInactive()
    {
        var store = new FakeStore();
        var carol = store.AddUser(2, userName: "carol");
        store.AddKey(11, carol);
        carol.Archive();

        var page = await AdminList(store).Handle(new GetAllApiKeysQuery(), CancellationToken.None);

        var entry = page.Items.Should().ContainSingle().Subject;
        entry.UserName.Should().Be("carol");
        entry.IsActive.Should().BeFalse();
    }

    [TestCase("revoked")]
    [TestCase("expired")]
    [TestCase("expires-now")]
    [TestCase("owner-disabled")]
    [TestCase("owner-archived")]
    [TestCase("owner-missing")]
    [TestCase("legacy-past-the-enforced-maximum")]
    public async Task Listings_CallAKeyActiveOnlyWhenValidationWouldAcceptIt(string state)
    {
        var store = new FakeStore();
        var owner = store.AddUser(1, userName: "admin", isSuperAdmin: true);
        var key = store.AddKey(11, owner, expiresAt: Now.UtcDateTime.AddDays(1));
        var options = new ApiKeyOptions { MaxLifetimeDays = 30, EnforceMaxLifetimeOnExistingKeys = state.StartsWith("legacy") };
        switch (state)
        {
            case "revoked":
                key.IsRevoked = true;
                break;
            case "expired":
                key.ExpiresAt = Now.UtcDateTime.AddSeconds(-1);
                break;
            case "expires-now":
                key.ExpiresAt = Now.UtcDateTime;
                break;
            case "owner-disabled":
                owner.IsEnabled = false;
                break;
            case "owner-archived":
                owner.Archive();
                break;
            case "owner-missing":
                key.User = null;
                break;
            default:
                key.ExpiresAt = null;
                key.CreatedTime = Now.UtcDateTime.AddDays(-31);
                break;
        }

        var adminEntry = (await AdminList(store, options).Handle(new GetAllApiKeysQuery(), CancellationToken.None)).Items.Single();
        var validated = await BuildApiKeys(store, options).ValidateApiKeyAsync(PlainTextKey, CancellationToken.None);

        adminEntry.IsActive.Should().BeFalse();
        validated.Should().BeNull("the listing and validation apply one rule");
    }

    [TestCase(false, true)]
    [TestCase(true, false)]
    public async Task OwnList_CallsAKeyActiveByTheSameRule(bool ownerDisabled, bool active)
    {
        var store = new FakeStore();
        var owner = store.AddUser(1, userName: "admin", isSuperAdmin: true);
        store.AddKey(11, owner, expiresAt: Now.UtcDateTime.AddDays(1));
        var users = AdminUsers(enabled: !ownerDisabled);
        var handler = new GetApiKeysHandler(BuildFactory(store), Accessor(CookiePrincipal("admin")), users.Object, Options.Create(new ApiKeyOptions()), new FakeTimeProvider(Now));

        var page = await handler.Handle(new GetApiKeysQuery(), CancellationToken.None);

        page.Items.Should().ContainSingle().Which.IsActive.Should().Be(active);
    }

    [Test]
    public async Task OwnList_ShowsOnlyTheCallersKeys_WithTheirScopesAndProjects()
    {
        var store = new FakeStore();
        var admin = store.AddUser(1, userName: "admin", isSuperAdmin: true);
        var other = store.AddUser(2);
        store.AddKey(11, admin, scopes: "[\"Read\",\"Admin\"]", projects: "[4]", expiresAt: Now.UtcDateTime.AddDays(1));
        store.AddKey(12, other);
        var handler = new GetApiKeysHandler(BuildFactory(store), Accessor(CookiePrincipal("admin")), AdminUsers().Object, Options.Create(new ApiKeyOptions()), new FakeTimeProvider(Now));

        var page = await handler.Handle(new GetApiKeysQuery(), CancellationToken.None);

        var entry = page.Items.Should().ContainSingle().Subject;
        entry.Id.Should().Be(11);
        entry.Scopes.Should().Equal("Read", "Execute");
        entry.AllowedProjectIds.Should().Equal(4);
        entry.IsActive.Should().BeTrue();
    }

    [Test]
    public async Task OwnListQuery_FiltersOnTheCallersUserId_Translates()
    {
        // The handler's own query against the Npgsql provider, without a database.
        var capture = new SqlCapture().ThenScalar(1).ThenNoRows();
        var handler = new GetApiKeysHandler(capture.Factory(), Accessor(CookiePrincipal("admin")), AdminUsers().Object, Options.Create(new ApiKeyOptions()), new FakeTimeProvider(Now));

        await handler.Handle(new GetApiKeysQuery(), CancellationToken.None);

        var sql = capture.Commands.Should().HaveCount(2).And.Subject.Last();
        sql.Should().Contain("FROM beacon.api_key_credentials");
        sql.Should().Contain("WHERE a.user_id = @");
        sql.Should().Contain("allowed_project_ids");
        sql.Should().Contain("ORDER BY a.created_time DESC");
    }

    [Test]
    public async Task LegacyKeyWithoutExpiry_StaysActive_UnlessTheMaximumIsEnforced()
    {
        var store = new FakeStore();
        var owner = store.AddUser(1, userName: "admin", isSuperAdmin: true);
        var key = store.AddKey(11, owner);
        key.CreatedTime = Now.UtcDateTime.AddDays(-400);

        var kept = await AdminList(store).Handle(new GetAllApiKeysQuery(), CancellationToken.None);
        var keptValid = await BuildApiKeys(store).ValidateApiKeyAsync(PlainTextKey, CancellationToken.None);

        kept.Items.Single().IsActive.Should().BeTrue();
        keptValid.Should().NotBeNull();
    }

    [Test]
    public async Task AdminListQuery_KeepsArchivedOwners_Translates()
    {
        // The handler's own query, run against the Npgsql provider without a database: the owner join must not drop
        // archived users, and their state is read from the join.
        var capture = new SqlCapture().ThenScalar(1).ThenNoRows();
        var handler = new GetAllApiKeysHandler(capture.Factory(), Accessor(CookiePrincipal("admin")), Options.Create(new ApiKeyOptions()), new FakeTimeProvider(Now));

        await handler.Handle(new GetAllApiKeysQuery { UserId = 2 }, CancellationToken.None);

        var sql = capture.Commands.Should().HaveCount(2).And.Subject.Last();
        sql.Should().Contain("FROM beacon.api_key_credentials");
        sql.Should().Contain("LEFT JOIN beacon.users");
        sql.Should().Contain("user_id");
        sql.Should().Contain("archived_time IS NOT NULL");
        sql.Should().NotContain("archived_time IS NULL");
    }

    [TestCase("GET", "/beacon/api/api-keys/admin")]
    [TestCase("DELETE", "/beacon/api/api-keys/admin/21")]
    public async Task AdminEndpoints_AdmitOnlyTheAdminRole_InASignedInSession(string method, string path)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetAllApiKeysQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedList<AdminApiKeyEntry>.Create([], 0, 20));
        await using var app = await StartApiAsync(mediator);

        var admin = await SendAsync(app, method, path, principal: "admin");
        var editor = await SendAsync(app, method, path, principal: "editor");
        var bearerAdmin = await SendAsync(app, method, path, principal: "jwt-admin");
        var executeKey = await SendAsync(app, method, path, apiKey: PlainTextKey);

        admin.IsSuccessStatusCode.Should().BeTrue();
        editor.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        bearerAdmin.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a bearer token is not a signed-in session");
        executeKey.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an API key never carries a role");
        mediator.Invocations.Should().HaveCount(1, "only the administrator's request reaches the handler");
    }

    [TestCase("GET", "/beacon/api/api-keys", null)]
    [TestCase("POST", "/beacon/api/api-keys", "{\"name\":\"ci\",\"scopes\":[\"Read\"]}")]
    [TestCase("DELETE", "/beacon/api/api-keys/5", null)]
    public async Task OwnKeyEndpoints_RefuseKeysAndBearerTokens(string method, string path, string? json)
    {
        var mediator = new Mock<IMediator>();
        await using var app = await StartApiAsync(mediator);

        var executeKey = await SendAsync(app, method, path, apiKey: PlainTextKey, json: json);
        var bearer = await SendAsync(app, method, path, principal: "jwt-admin", json: json);

        executeKey.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        bearer.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        mediator.Invocations.Should().BeEmpty("keys are managed from a signed-in session only");
    }

    private static UserManagementService BuildUsers(FakeStore store) =>
        new(BuildFactory(store), Mock.Of<IPasswordHasher>(), Mock.Of<IRoleService>(), new BeaconConfiguration(), new FakeTimeProvider(Now));

    private static ApiKeyService BuildApiKeys(FakeStore store, ApiKeyOptions? options = null) =>
        new(BuildFactory(store), Options.Create(options ?? new ApiKeyOptions()), new FakeTimeProvider(Now), NullLogger<ApiKeyService>.Instance);

    private static AdminRevokeApiKeyHandler AdminRevoke(FakeStore store) =>
        new(BuildApiKeys(store), Accessor(CookiePrincipal("admin")), AdminUsers().Object, NullLogger<AdminRevokeApiKeyHandler>.Instance);

    private static GetAllApiKeysHandler AdminList(FakeStore store, ApiKeyOptions? options = null) =>
        new(BuildFactory(store), Accessor(CookiePrincipal("admin")), Options.Create(options ?? new ApiKeyOptions()), new FakeTimeProvider(Now));

    // The signed-in administrator behind CookiePrincipal: Users.ExternalId "ext-1", Beacon user id 1.
    private static Mock<IUserManagementService> AdminUsers(bool enabled = true)
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.GetUserByExternalIdAsync("ext-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData { Id = 1, ExternalId = "ext-1", UserName = "admin", IsSuperAdmin = true, IsEnabled = enabled });
        return users;
    }

    private static IHttpContextAccessor Accessor(ClaimsPrincipal principal) =>
        new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

    private static Core.Models.UserManagement.UpdateUserRequest Update(int userId, bool isEnabled) =>
        new() { UserId = userId, UserName = $"user{userId}", IsEnabled = isEnabled };

    private static Task<HttpResponseMessage> SendAsync(WebApplication app, string method, string path, string? principal = null, string? apiKey = null, string? json = null)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        if (principal == "jwt-admin")
        {
            // A REST bearer caller: its middleware's identity and marker, its header, and no antiforgery token.
            request.Headers.Add(TestPrincipalHeader, principal);
            request.Headers.Add("Authorization", "Bearer header.payload.signature");
        }
        else if (principal != null)
        {
            request.Headers.Add(TestPrincipalHeader, principal);

            // A cookie session's mutation carries the antiforgery pair GET /beacon/api/csrf would have issued.
            var tokens = app.Services
                .GetRequiredService<IAntiforgery>()
                .GetAndStoreTokens(new DefaultHttpContext { RequestServices = app.Services, User = CookiePrincipal(principal) });
            request.Headers.Add("Cookie", $"{AntiforgeryCookieName}={tokens.CookieToken}");
            request.Headers.Add("X-XSRF-TOKEN", tokens.RequestToken);
        }

        if (apiKey != null)
        {
            request.Headers.Add("Authorization", $"Bearer {apiKey}");
        }

        return app.GetTestClient().SendAsync(request);
    }

    private static async Task<WebApplication> StartApiAsync(Mock<IMediator> mediator)
    {
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(PlainTextKey, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCredential
            {
                Id = 3,
                UserId = 1,
                Name = "automation",
                KeyHash = "hash",
                KeyPrefix = "sk-sem_",
                Scopes = "[\"Execute\"]",
                User = new BeaconUser { Id = 1, UserName = "root", ExternalId = "ext-1", IsEnabled = true }
            });

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mediator.Object);
        builder.Services.AddSingleton(apiKeys.Object);
        builder.Services.AddSingleton(new BeaconConfiguration());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>());
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IUserManagementService>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false });
        builder.Services.AddAntiforgery(x =>
        {
            x.Cookie.Name = AntiforgeryCookieName;
            x.HeaderName = "X-XSRF-TOKEN";
        });
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, StatusCodeResultHandler>();

        var app = builder.Build();
        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.Use((context, next) =>
        {
            // Stands in for the cookie middleware.
            if (context.Request.Headers.TryGetValue(TestPrincipalHeader, out var kind))
            {
                context.User = CookiePrincipal(kind.ToString());
            }

            return next(context);
        });
        app.UseRouting();
        app.UseAuthorization();
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }

    private static ClaimsPrincipal CookiePrincipal(string role, params Claim[] extra) =>
        role == "jwt-admin"
            ? new(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ext-1"), new Claim(ClaimTypes.Role, "Admin"), new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.JwtAuthMethod)],
                "Bearer"))
            : new(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ext-1"), new Claim(ClaimTypes.Role, role == "admin" ? "Admin" : "Editor"), .. extra],
                "Beacon.Auth"));

    private static IDbContextFactory<BeaconContext> BuildFactory(FakeStore store)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new FakeBeaconContext(store));
        return factory.Object;
    }

    private static string Hash(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private sealed record SaveSnapshot(bool UserEnabled, bool UserArchived, int ActiveKeysOfUser);

    private sealed class FakeStore
    {
        private int _trackedUserId;

        public List<BeaconUser> Users { get; } = [];

        public List<ApiKeyCredential> Keys { get; } = [];

        /// <summary>The tracked user (the first one added) and their active keys, as each save sees them.</summary>
        public List<SaveSnapshot> Saves { get; } = [];

        public BeaconUser AddUser(int id, bool isSuperAdmin = false, string? userName = null, bool enabled = true)
        {
            var user = new BeaconUser { Id = id, ExternalId = $"ext-{id}", UserName = userName ?? $"user{id}", IsEnabled = enabled, IsSuperAdmin = isSuperAdmin };
            if (Users.Count == 0)
            {
                _trackedUserId = id;
            }

            Users.Add(user);
            return user;
        }

        public ApiKeyCredential AddKey(int id, BeaconUser owner, DateTime? revokedAt = null, string scopes = "[\"Read\"]", string? projects = null, DateTime? expiresAt = null)
        {
            var key = new ApiKeyCredential
            {
                Id = id,
                UserId = owner.Id,
                User = owner,
                Name = $"key{id}",
                KeyHash = Keys.Count == 0 ? Hash(PlainTextKey) : $"hash-{id}",
                KeyPrefix = PlainTextKey[..16],
                Scopes = scopes,
                AllowedProjectIds = projects,
                IsRevoked = revokedAt != null,
                RevokedAt = revokedAt,
                ExpiresAt = expiresAt,
                CreatedTime = Now.UtcDateTime.AddDays(-1)
            };
            Keys.Add(key);
            return key;
        }

        public void RecordSave()
        {
            var user = Users.First(x => x.Id == _trackedUserId);
            Saves.Add(new SaveSnapshot(
                user.IsEnabled,
                user.ArchivedTime != null,
                Keys.Count(x => x.UserId == user.Id && !x.IsRevoked)));
        }
    }

    private sealed class FakeBeaconContext(FakeStore store) : BeaconContext(ContextOptions, "beacon")
    {
        private static readonly DbContextOptions<FakeBeaconContext> ContextOptions =
            new DbContextOptionsBuilder<FakeBeaconContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        public override DbSet<TEntity> Set<TEntity>()
        {
            if (typeof(TEntity) == typeof(BeaconUser))
            {
                return (DbSet<TEntity>)(object)BuildSet(store.Users, x => x.Id);
            }

            if (typeof(TEntity) == typeof(ApiKeyCredential))
            {
                return (DbSet<TEntity>)(object)BuildSet(store.Keys, x => x.Id);
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            store.RecordSave();
            return Task.FromResult(0);
        }

        public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
            SaveChangesAsync(cancellationToken);
    }

    private static DbSet<T> BuildSet<T>(List<T> backing, Func<T, int> key) where T : class
    {
        var data = backing.AsQueryable();
        var set = new Mock<DbSet<T>>();
        set.As<IAsyncEnumerable<T>>()
            .Setup(x => x.GetAsyncEnumerator(It.IsAny<CancellationToken>()))
            .Returns(() => new TestAsyncEnumerator<T>(backing.GetEnumerator()));
        set.As<IQueryable<T>>().Setup(x => x.Provider).Returns(new TestAsyncQueryProvider<T>(data.Provider));
        set.As<IQueryable<T>>().Setup(x => x.Expression).Returns(data.Expression);
        set.As<IQueryable<T>>().Setup(x => x.ElementType).Returns(data.ElementType);
        set.As<IQueryable<T>>().Setup(x => x.GetEnumerator()).Returns(() => backing.GetEnumerator());
        set.Setup(x => x.FindAsync(It.IsAny<object?[]?>(), It.IsAny<CancellationToken>()))
            .Returns((object?[]? keys, CancellationToken _) =>
                new ValueTask<T?>(backing.FirstOrDefault(x => key(x) == (int)keys![0]!)));
        return set.Object;
    }

    /// <summary>Writes 401/403 directly so no authentication scheme is needed for Forbid/Challenge.</summary>
    private sealed class StatusCodeResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Succeeded)
            {
                return next(context);
            }

            context.Response.StatusCode = authorizeResult.Forbidden
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }
}
