using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC14 — <see cref="DatabaseAuthorizationProvider"/> must find the Beacon user behind every principal shape that
/// reaches <c>/beacon/api</c>, or enforcement would 403 every caller. API keys put the numeric user id in
/// <c>NameIdentifier</c> and the username in <c>username</c>; cookie logins and OIDC put <c>Users.ExternalId</c> /
/// the subject in <c>NameIdentifier</c>, while a host's claims transformation may set <see cref="BeaconClaims.UserId"/>
/// to the username — so that claim must never be the lookup key.
/// </summary>
[TestFixture]
public class DatabaseAuthorizationProviderIdentityTests
{
    private Mock<IUserManagementService> _users = null!;

    [SetUp]
    public void SetUp()
    {
        _users = new Mock<IUserManagementService>(MockBehavior.Strict);
    }

    [Test]
    public async Task ApiKeyPrincipal_ResolvesByUsernameClaim()
    {
        _users
            .Setup(x => x.GetUserByUserNameAsync("alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(RoleService.RoleLevels.Editor));
        var provider = BuildProvider(Principal(
            "ApiKey",
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim("auth_method", "api_key"),
            new Claim("username", "alice")));

        var canWrite = await provider.HasWritePermissionAsync();

        canWrite.Should().BeTrue("the key's numeric user id is not an ExternalId; the username is the lookup key");
    }

    [Test]
    public async Task ForgedApiKeyClaims_OnNonApiKeyIdentity_ResolveByNameIdentifier_NeverByUsername()
    {
        var externalId = Guid.NewGuid().ToString();
        _users
            .Setup(x => x.GetUserByExternalIdAsync(externalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(RoleService.RoleLevels.Viewer));
        var provider = BuildProvider(Principal(
            "Cookies",
            new Claim(ClaimTypes.NameIdentifier, externalId),
            new Claim("auth_method", "api_key"),
            new Claim("username", "victim")));

        var canRead = await provider.HasReadPermissionAsync();

        canRead.Should().BeTrue();
        _users.Verify(x => x.GetUserByExternalIdAsync(externalId, It.IsAny<CancellationToken>()), Times.Once);
        _users.VerifyNoOtherCalls();
    }

    [Test]
    public async Task SampleCookiePrincipal_ResolvesByNameIdentifier_NotByBeaconUserIdClaim()
    {
        var externalId = Guid.NewGuid().ToString();
        _users
            .Setup(x => x.GetUserByExternalIdAsync(externalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(RoleService.RoleLevels.Viewer));
        var provider = BuildProvider(Principal(
            "Cookies",
            new Claim(ClaimTypes.NameIdentifier, externalId),
            new Claim(ClaimTypes.Name, "alice"),
            new Claim(BeaconClaims.UserId, "alice")));

        var canRead = await provider.HasReadPermissionAsync();

        canRead.Should().BeTrue("the claims transformation sets beacon:user_id to the username, which is not an ExternalId");
    }

    [Test]
    public async Task OidcPrincipal_ResolvesBySubject()
    {
        _users
            .Setup(x => x.GetUserByExternalIdAsync("oidc-subject-123", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(RoleService.RoleLevels.Editor));
        var provider = BuildProvider(Principal(
            "Cookies",
            new Claim(ClaimTypes.NameIdentifier, "oidc-subject-123")));

        var canWrite = await provider.HasWritePermissionAsync();

        canWrite.Should().BeTrue();
    }

    [Test]
    public async Task Viewer_CanReadButNotWrite()
    {
        var provider = BuildCookieProviderFor(User(RoleService.RoleLevels.Viewer));

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeTrue();
        canWrite.Should().BeFalse();
    }

    [Test]
    public async Task Editor_CanReadAndWrite()
    {
        var provider = BuildCookieProviderFor(User(RoleService.RoleLevels.Editor));

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeTrue();
        canWrite.Should().BeTrue();
    }

    [Test]
    public async Task DisabledUser_IsDenied()
    {
        var user = User(RoleService.RoleLevels.Admin);
        user.IsEnabled = false;
        var provider = BuildCookieProviderFor(user);

        var canRead = await provider.HasReadPermissionAsync();

        canRead.Should().BeFalse();
    }

    [Test]
    public async Task UserlessApiKey_IsDenied_WithoutAnExternalIdLookup()
    {
        // NameIdentifier carries the key id here; it must never be matched against Users.ExternalId.
        // The strict mock fails the test on any lookup.
        var provider = BuildProvider(Principal(
            "ApiKey",
            new Claim(ClaimTypes.NameIdentifier, "7"),
            new Claim("auth_method", "api_key")));

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeFalse();
        canWrite.Should().BeFalse();
    }

    [Test]
    public async Task UnknownExternalId_IsDeniedReadAndWrite()
    {
        _users
            .Setup(x => x.GetUserByExternalIdAsync("ghost", It.IsAny<CancellationToken>()))
            .ReturnsAsync((BeaconUserData?)null);
        var provider = BuildProvider(Principal("Cookies", new Claim(ClaimTypes.NameIdentifier, "ghost")));

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeFalse();
        canWrite.Should().BeFalse();
    }

    [Test]
    public async Task UserWithNoRoles_IsDenied()
    {
        var user = User(RoleService.RoleLevels.Viewer);
        user.Roles = [];
        var provider = BuildCookieProviderFor(user);

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeFalse();
        canWrite.Should().BeFalse();
    }

    [Test]
    public async Task SuperAdminWithNoRoles_IsAllowed()
    {
        var user = User(RoleService.RoleLevels.Viewer);
        user.Roles = [];
        user.IsSuperAdmin = true;
        var provider = BuildCookieProviderFor(user);

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeTrue();
        canWrite.Should().BeTrue();
    }

    [Test]
    public async Task ApiKeyPrincipal_WhoseUserIsDisabled_IsDenied()
    {
        var user = User(RoleService.RoleLevels.Admin);
        user.IsEnabled = false;
        _users
            .Setup(x => x.GetUserByUserNameAsync("alice", It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);
        var provider = BuildProvider(Principal(
            "ApiKey",
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim("auth_method", "api_key"),
            new Claim("username", "alice")));

        var canRead = await provider.HasReadPermissionAsync();
        var canWrite = await provider.HasWritePermissionAsync();

        canRead.Should().BeFalse();
        canWrite.Should().BeFalse();
    }

    [Test]
    public async Task NoHttpContext_IsDenied()
    {
        var provider = new DatabaseAuthorizationProvider(new HttpContextAccessor(), _users.Object);

        var canRead = await provider.HasReadPermissionAsync();

        canRead.Should().BeFalse();
    }

    [Test]
    public async Task UserIsLookedUpOncePerScope()
    {
        _users
            .Setup(x => x.GetUserByExternalIdAsync("sub-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(User(RoleService.RoleLevels.Editor));
        var provider = BuildProvider(Principal("Cookies", new Claim(ClaimTypes.NameIdentifier, "sub-1")));

        await provider.HasReadPermissionAsync();
        await provider.HasWritePermissionAsync();

        _users.Verify(x => x.GetUserByExternalIdAsync("sub-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    private DatabaseAuthorizationProvider BuildCookieProviderFor(BeaconUserData user)
    {
        _users
            .Setup(x => x.GetUserByExternalIdAsync(user.ExternalId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        return BuildProvider(Principal("Cookies", new Claim(ClaimTypes.NameIdentifier, user.ExternalId)));
    }

    private DatabaseAuthorizationProvider BuildProvider(ClaimsPrincipal principal)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };

        return new DatabaseAuthorizationProvider(accessor, _users.Object);
    }

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType));

    private static BeaconUserData User(int roleLevel) =>
        new()
        {
            Id = 42,
            ExternalId = Guid.NewGuid().ToString(),
            UserName = "alice",
            IsEnabled = true,
            Roles = [new BeaconRoleData { Name = "role", Level = roleLevel }]
        };
}
