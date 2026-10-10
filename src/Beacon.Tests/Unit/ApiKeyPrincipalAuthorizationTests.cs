using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// W1 — the principal <see cref="ApiKeyAuthMiddleware"/> mints and the claims <see cref="DatabaseAuthorizationProvider"/>
/// reads live in different layers; this runs the real middleware and feeds its <c>context.User</c> to the real provider
/// so a drift in the claim names (or the authentication type) cannot go unnoticed. Also the other direction: the
/// middleware asks the authorization provider whether an Execute key's owner may write, presenting the owner as their
/// signed-in session would, and restores the request's user afterwards.
/// </summary>
[TestFixture]
public class ApiKeyPrincipalAuthorizationTests
{
    private const string UserName = "alice";

    [TestCase(RoleService.RoleLevels.Viewer, true, false)]
    [TestCase(RoleService.RoleLevels.Editor, true, true)]
    public async Task KeyLinkedToUser_ResolvesTheUserByUserName_AndAppliesTheRole(int roleLevel, bool expectedRead, bool expectedWrite)
    {
        var users = new Mock<IUserManagementService>(MockBehavior.Strict);
        users
            .Setup(x => x.GetUserByUserNameAsync(UserName, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData
            {
                Id = 42,
                ExternalId = Guid.NewGuid().ToString(),
                UserName = UserName,
                IsEnabled = true,
                Roles = [new BeaconRoleData { Name = "role", Level = roleLevel }]
            });
        var context = await AuthenticateAsync(new ApiKeyCredential
        {
            Id = 7,
            Name = "ci",
            KeyHash = "hash",
            KeyPrefix = "sk-sem_",
            Scopes = "[\"Execute\"]",
            UserId = 42,
            User = new BeaconUser { UserName = UserName, ExternalId = "ext" }
        });
        var provider = new DatabaseAuthorizationProvider(new HttpContextAccessor { HttpContext = context }, users.Object);

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().Be(expectedRead);
        canWrite.Should().Be(expectedWrite);
        users.Verify(x => x.GetUserByUserNameAsync(UserName, It.IsAny<CancellationToken>()), Times.Once);
        users.VerifyNoOtherCalls();
    }

    [Test]
    public async Task UserlessKey_FromAReplacedKeyStore_IsRefusedByTheMiddleware()
    {
        // The built-in key store never returns an ownerless key; a replaced one must not make the key id stand in for
        // a user id.
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCredential
            {
                Id = 7,
                Name = "ci",
                KeyHash = "hash",
                KeyPrefix = "sk-sem_",
                Scopes = "[\"Execute\"]"
            });
        // The 401 problem response needs the host's services to write itself.
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().BuildServiceProvider()
        };
        context.Request.Headers.Authorization = "Bearer sk-sem_test-key";
        var nextInvoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, apiKeys.Object, NullLogger<ApiKeyAuthMiddleware>.Instance);

        nextInvoked.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        context.User.Identity?.IsAuthenticated.Should().NotBe(true);
        apiKeys.Verify(x => x.UpdateLastUsedAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ExecuteKey_AsksTheProviderAboutTheOwnerAsTheirSession_ThenCarriesTheKey()
    {
        ClaimsPrincipal? asked = null;
        var context = KeyRequest();
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization
            .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
            .Callback(() => asked = context.User)
            .ReturnsAsync(true);
        UseProvider(context, authorization.Object);

        var nextInvoked = await InvokeMiddlewareAsync(context, EditorsExecuteKey());

        nextInvoked.Should().BeTrue();
        asked.Should().NotBeNull("the provider decides whether the key keeps Execute");
        asked!.Identity!.AuthenticationType.Should().Be(McpCallerClaimTypes.ApiKeyOwnerAuthenticationType);
        asked.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("ext-alice", "a session carries Users.ExternalId");
        asked.FindFirst(ClaimTypes.Name)!.Value.Should().Be(UserName);
        asked.FindAll(ClaimTypes.Role).Select(x => x.Value).Should().Equal("Editor");
        asked.HasClaim(x => x.Type == McpCallerClaimTypes.AuthMethod).Should().BeFalse();
        context.User.Identity!.AuthenticationType.Should().Be(McpCallerClaimTypes.ApiKeyAuthenticationType);
        context.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("42");
        context.User.FindAll(McpCallerClaimTypes.Scope).Select(x => x.Value).Should().Equal("Execute");
    }

    [Test]
    public async Task ExecuteKey_ActsAsAReadKey_WhenTheProviderDeniesTheOwnerWrite()
    {
        var context = KeyRequest();
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization
            .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        UseProvider(context, authorization.Object);

        var nextInvoked = await InvokeMiddlewareAsync(context, EditorsExecuteKey());

        nextInvoked.Should().BeTrue("the key still reads");
        context.User.Identity!.AuthenticationType.Should().Be(McpCallerClaimTypes.ApiKeyAuthenticationType);
        context.User.FindAll(McpCallerClaimTypes.Scope).Select(x => x.Value).Should().Equal("Read");
    }

    [TestCase(RoleService.RoleLevels.Editor, "Execute")]
    [TestCase(RoleService.RoleLevels.Viewer, "Read")]
    public async Task DatabaseProvider_IsAskedAboutTheOwner_ByTheirExternalId(int storedRoleLevel, string scope)
    {
        var users = new Mock<IUserManagementService>(MockBehavior.Strict);
        users
            .Setup(x => x.GetUserByExternalIdAsync("ext-alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData
            {
                Id = 42,
                ExternalId = "ext-alice",
                UserName = UserName,
                IsEnabled = true,
                Roles = [new BeaconRoleData { Name = "role", Level = storedRoleLevel }]
            });
        var context = KeyRequest();
        UseProvider(context, new DatabaseAuthorizationProvider(new HttpContextAccessor { HttpContext = context }, users.Object));

        await InvokeMiddlewareAsync(context, EditorsExecuteKey());

        context.User.FindAll(McpCallerClaimTypes.Scope).Select(x => x.Value).Should().Equal(scope);
        users.Verify(x => x.GetUserByExternalIdAsync("ext-alice", It.IsAny<CancellationToken>()), Times.Once);
        users.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ProviderFailure_PropagatesAndLeavesTheRequestUnauthenticated()
    {
        var context = KeyRequest();
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization
            .Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider unavailable"));
        UseProvider(context, authorization.Object);

        var act = () => InvokeMiddlewareAsync(context, EditorsExecuteKey());

        await act.Should().ThrowAsync<InvalidOperationException>();
        context.User.Identity?.IsAuthenticated.Should().NotBe(true, "the owner's stand-in must not outlive the provider call");
    }

    private static DefaultHttpContext KeyRequest()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer sk-sem_test-key";
        return context;
    }

    private static void UseProvider(HttpContext context, IBeaconAuthorizationProvider authorization)
    {
        context.RequestServices = new ServiceCollection()
            .AddSingleton(authorization)
            .BuildServiceProvider();
    }

    private static async Task<bool> InvokeMiddlewareAsync(HttpContext context, ApiKeyCredential credential)
    {
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);
        var nextInvoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, apiKeys.Object, NullLogger<ApiKeyAuthMiddleware>.Instance);

        return nextInvoked;
    }

    // An Execute key whose owner holds the Editor role, as the key store loads it.
    private static ApiKeyCredential EditorsExecuteKey() =>
        new()
        {
            Id = 7,
            Name = "ci",
            KeyHash = "hash",
            KeyPrefix = "sk-sem_",
            Scopes = "[\"Execute\"]",
            UserId = 42,
            User = new BeaconUser
            {
                Id = 42,
                UserName = UserName,
                ExternalId = "ext-alice",
                IsEnabled = true,
                UserRoles = [new BeaconUserRole { UserId = 42, Role = new BeaconRole { Name = "Editor", Level = RoleService.RoleLevels.Editor } }]
            }
        };

    private static async Task<HttpContext> AuthenticateAsync(ApiKeyCredential credential)
    {
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(credential);
        var context = new DefaultHttpContext();
        context.Request.Headers.Authorization = "Bearer sk-sem_test-key";
        var nextInvoked = false;
        var middleware = new ApiKeyAuthMiddleware(_ =>
        {
            nextInvoked = true;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(context, apiKeys.Object, NullLogger<ApiKeyAuthMiddleware>.Instance);

        nextInvoked.Should().BeTrue("the middleware must have authenticated the key");
        context.User.Identity?.IsAuthenticated.Should().BeTrue();

        return context;
    }
}
