using Beacon.Api.Authentication;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Data.Entities;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// W1 — the principal <see cref="ApiKeyAuthMiddleware"/> mints and the claims <see cref="DatabaseAuthorizationProvider"/>
/// reads live in different layers; this runs the real middleware and feeds its <c>context.User</c> to the real provider
/// so a drift in the claim names (or the authentication type) cannot go unnoticed.
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
    public async Task UserlessKey_NeverLooksUpAUser_AndIsDenied()
    {
        // Strict mock: any lookup (by user name or by external id) fails the test.
        var users = new Mock<IUserManagementService>(MockBehavior.Strict);
        var context = await AuthenticateAsync(new ApiKeyCredential
        {
            Id = 7,
            Name = "ci",
            KeyHash = "hash",
            KeyPrefix = "sk-sem_",
            Scopes = "[\"Execute\"]"
        });
        var provider = new DatabaseAuthorizationProvider(new HttpContextAccessor { HttpContext = context }, users.Object);

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeFalse();
        canWrite.Should().BeFalse();
        users.VerifyNoOtherCalls();
    }

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
