using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Api.Authentication;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

[TestFixture]
public class OidcEventHandlersTests
{
    private const string TenantId = "tenant-1";

    [Test]
    public async Task HandleTokenValidatedAsync_UnknownSub_CallsGetOrCreateAndEnrichesClaims()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                "sub-alice",
                "https://login.example.com/",
                It.IsAny<string>(),
                "alice@example.com",
                "Alice Example",
                "Viewer",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData
            {
                Id = 42,
                ExternalId = "sub-alice",
                IdentityProvider = "https://login.example.com/",
                UserName = "alice@example.com",
                Email = "alice@example.com",
                DisplayName = "Alice Example",
                IsInternalUser = false,
                IsEnabled = true,
                Roles = new List<BeaconRoleData>
                {
                    new() { Id = 3, Name = "Viewer", Level = 1 }
                }
            });

        var context = BuildContext(
            userService.Object,
            sub: "sub-alice",
            iss: "https://login.example.com/",
            email: "alice@example.com",
            name: "Alice Example",
            preferredUsername: "alice@example.com",
            existingRoleFromIdp: "SpoofedAdmin");

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result.Should().BeNull();

        var identity = context.Principal!.Identities.First();
        identity.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("sub-alice");
        identity.FindFirst(ClaimTypes.Email)!.Value.Should().Be("alice@example.com");
        identity.FindFirst("DisplayName")!.Value.Should().Be("Alice Example");

        var roles = identity.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        roles.Should().Equal("Viewer");

        identity.FindFirst(BeaconClaims.UserId)!.Value.Should().Be("sub-alice");
        identity.FindFirst(BeaconClaims.UserName)!.Value.Should().Be("alice@example.com");
        var semRoles = identity.FindAll(BeaconClaims.Role).Select(c => c.Value).ToList();
        semRoles.Should().Equal("Viewer");

        userService.VerifyAll();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_NoSubClaim_Fails()
    {
        var userService = new Mock<IUserManagementService>(MockBehavior.Strict);

        var context = BuildContext(
            userService.Object,
            sub: null,
            iss: "https://login.example.com/",
            email: "alice@example.com",
            name: "Alice",
            preferredUsername: null,
            existingRoleFromIdp: null);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result.Should().NotBeNull();
        context.Result!.Failure.Should().NotBeNull();
        userService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_DisabledUser_FailsAndDoesNotEnrich()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("This account has been disabled."));

        var context = BuildContext(
            userService.Object,
            sub: "sub-bob",
            iss: "https://login.example.com/",
            email: "bob@example.com",
            name: "Bob",
            preferredUsername: "bob",
            existingRoleFromIdp: null);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result.Should().NotBeNull();
        context.Result!.Failure.Should().BeOfType<OidcNotAdmittedException>(
            "a disabled account gets the same not-admitted answer as any refused subject");
        context.Principal!.FindAll(BeaconClaims.UserId).Should().BeEmpty();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_MissingPreferredUsername_FallsBackToEmail()
    {
        string? usernamePassedToService = null;
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, string, string, string?, string?, string, CancellationToken>(
                (_, _, userName, _, _, _, _) => usernamePassedToService = userName)
            .ReturnsAsync(new BeaconUserData
            {
                Id = 1,
                ExternalId = "sub-c",
                UserName = "carol@example.com",
                Email = "carol@example.com",
                Roles = new List<BeaconRoleData>()
            });

        var context = BuildContext(
            userService.Object,
            sub: "sub-c",
            iss: "https://login.example.com/",
            email: "carol@example.com",
            name: "Carol",
            preferredUsername: null,
            existingRoleFromIdp: null);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        usernamePassedToService.Should().Be("carol@example.com");
    }

    [Test]
    public async Task HandleTokenValidatedAsync_ForeignTenant_IsNotAdmitted_AndNothingIsProvisioned()
    {
        var userService = new Mock<IUserManagementService>(MockBehavior.Strict);
        var context = BuildContext(
            userService.Object,
            sub: "sub-eve",
            iss: "https://login.example.com/",
            email: "eve@other.example",
            name: "Eve",
            preferredUsername: "eve",
            existingRoleFromIdp: null,
            tenantId: "tenant-other");

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result!.Failure.Should().BeOfType<OidcNotAdmittedException>();
        userService.VerifyNoOtherCalls();
    }

    [TestCase("acct", "1")]
    [TestCase("idp", "https://sts.windows.net/home-tenant-of-the-guest/")]
    [TestCase("idp", "live.com")]
    public async Task HandleTokenValidatedAsync_Guest_IsNotAdmitted_AndNothingIsProvisioned(string claimType, string value)
    {
        var userService = new Mock<IUserManagementService>(MockBehavior.Strict);
        var context = BuildContext(
            userService.Object,
            sub: "sub-guest",
            iss: "https://login.microsoftonline.com/tenant-1/v2.0",
            email: "guest@partner.example",
            name: "Guest",
            preferredUsername: "guest",
            existingRoleFromIdp: null,
            extraClaims: [new Claim(claimType, value)]);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result!.Failure.Should().BeOfType<OidcNotAdmittedException>();
        userService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_WithoutARequiredRoleOrGroup_IsNotAdmitted()
    {
        var userService = new Mock<IUserManagementService>(MockBehavior.Strict);
        var context = BuildContext(
            userService.Object,
            sub: "sub-dan",
            iss: "https://login.example.com/",
            email: "dan@example.com",
            name: "Dan",
            preferredUsername: "dan",
            existingRoleFromIdp: null,
            extraClaims: [new Claim("roles", "Other.Role"), new Claim("groups", "unrelated-group")],
            configure: x =>
            {
                x.RequiredRoles = ["Beacon.User"];
                x.RequiredGroups = ["beacon-group"];
            });

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result!.Failure.Should().BeOfType<OidcNotAdmittedException>();
        userService.VerifyNoOtherCalls();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_NoDefaultRoleConfigured_ProvisionsTheUserWithoutARole()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                "sub-fay",
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                null,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData
            {
                Id = 7,
                ExternalId = "sub-fay",
                UserName = "fay",
                IsEnabled = true,
                Roles = []
            });
        var context = BuildContext(
            userService.Object,
            sub: "sub-fay",
            iss: "https://login.example.com/",
            email: "fay@example.com",
            name: "Fay",
            preferredUsername: "fay",
            existingRoleFromIdp: "Admin",
            extraClaims: [new Claim("roles", "Beacon.User")],
            configure: x =>
            {
                x.DefaultRoleName = null;
                x.RequiredRoles = ["Beacon.User"];
            });

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result.Should().BeNull("an admitted user signs in");
        context.Principal!.FindAll(ClaimTypes.Role).Should().BeEmpty("neither the IdP's role nor a default role is granted");
        context.Principal.FindAll(BeaconClaims.Role).Should().BeEmpty();
        context.Principal.FindAll("roles").Should().BeEmpty("the app role that admitted the user is not a Beacon role");
        context.Principal.FindFirst(BeaconClaims.UserId)!.Value.Should().Be("sub-fay");
        userService.VerifyAll();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_IdpRoles_UnderTheIdentitysOwnRoleClaimType_GrantNothing()
    {
        var userService = ProvisioningAs(Viewer("sub-gil"));
        var context = BuildContext(
            userService.Object,
            sub: "sub-gil",
            iss: "https://login.example.com/",
            email: null,
            name: "Gil",
            preferredUsername: "gil",
            existingRoleFromIdp: null,
            extraClaims:
            [
                new Claim("role", "Admin"),
                new Claim("roles", "Admin"),
                new Claim("groups", "admins"),
                new Claim("wids", "62e90394-69f5-4237-9190-012177145e10")
            ],
            roleClaimType: "role");

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result.Should().BeNull();
        var principal = context.Principal!;
        principal.IsInRole("Admin").Should().BeFalse("an identity-provider role is never a Beacon role");
        principal.IsInRole("Viewer").Should().BeTrue("the Beacon role is readable through the identity's role claim type");
        principal.FindAll(ClaimTypes.Role).Select(x => x.Value).Should().Equal("Viewer");
        principal.Claims.Select(x => x.Type).Should().NotContain(["roles", "groups", "wids"]);
    }

    [Test]
    public async Task HandleTokenValidatedAsync_TokenSuppliedBeaconClaims_NeverReachTheSession()
    {
        var userService = ProvisioningAs(Viewer("sub-hal"));
        var context = BuildContext(
            userService.Object,
            sub: "sub-hal",
            iss: "https://login.example.com/",
            email: null,
            name: "Hal",
            preferredUsername: "hal",
            existingRoleFromIdp: null,
            extraClaims:
            [
                new Claim(BeaconClaims.UserId, "1"),
                new Claim("BEACON:ROLE", "Admin"),
                new Claim(BeaconClaims.Permission, "users:write")
            ]);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        var principal = context.Principal!;
        principal.FindAll(BeaconClaims.UserId).Select(x => x.Value).Should().Equal("sub-hal");
        principal.FindAll(BeaconClaims.Role).Select(x => x.Value).Should().Equal("Viewer");
        principal.FindAll(BeaconClaims.Permission).Should().BeEmpty();
        context.HttpContext.User = principal;
        new HttpContextUserContext(new HttpContextAccessor { HttpContext = context.HttpContext })
            .UserId.Should().Be("sub-hal");
    }

    [Test]
    public async Task HandleTokenValidatedAsync_TokenSuppliedScopedCallerClaims_NeverReachTheSession()
    {
        // A browser session is never a scoped caller: whatever the identity provider sends under the claim types that
        // mark one (method, scope, projects, key, MCP caller) is removed, so the session is neither scope-gated nor
        // project-restricted by it and carries no key id.
        var userService = ProvisioningAs(Viewer("sub-ivy"));
        var context = BuildContext(
            userService.Object,
            sub: "sub-ivy",
            iss: "https://login.example.com/",
            email: null,
            name: "Ivy",
            preferredUsername: "ivy",
            existingRoleFromIdp: null,
            extraClaims:
            [
                new Claim("auth_method", "api_key"),
                new Claim("scope", "Execute"),
                new Claim("allowed_projects", "[1,2]"),
                new Claim("api_key_id", "42"),
                new Claim("api_key_name", "ci"),
                new Claim("caller_kind", "System"),
                new Claim("CALLER_HASH", "abc")
            ]);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result.Should().BeNull();
        var types = context.Principal!.Claims.Select(x => x.Type).ToList();
        types.Should().NotContain(x => McpCallerClaimTypes.Reserved.Contains(x));
        BeaconScopes.IsScopedCaller(context.Principal).Should().BeFalse();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_NotAdmitted_LogsTheReasonAndASubjectHash_NeverTheSubject()
    {
        var logs = new LogRecorder();
        var context = BuildContext(
            new Mock<IUserManagementService>(MockBehavior.Strict).Object,
            sub: "sub-ivy",
            iss: "https://login.example.com/",
            email: "ivy@example.com",
            name: "Ivy",
            preferredUsername: "ivy",
            existingRoleFromIdp: null,
            tenantId: "tenant-elsewhere",
            logs: logs);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        var entry = logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning).Subject;
        entry.Message.Should().Contain(nameof(OidcAdmissionDecision.TenantNotAllowed))
            .And.Contain("tenant-elsewhere")
            .And.Contain(SubjectFingerprint.Of("sub-ivy"));
        logs.Contains("sub-ivy").Should().BeFalse();
        logs.Contains("ivy@example.com").Should().BeFalse();
    }

    [Test]
    public async Task HandleTokenValidatedAsync_BeforeFirstRunSetup_IsNotAdmitted()
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("First-run setup has not been completed: users are provisioned only after the super admin exists."));
        var logs = new LogRecorder();
        var context = BuildContext(
            userService.Object,
            sub: "sub-jo",
            iss: "https://login.example.com/",
            email: null,
            name: "Jo",
            preferredUsername: "jo",
            existingRoleFromIdp: null,
            logs: logs);

        await OidcEventHandlers.HandleTokenValidatedAsync(context);

        context.Result!.Failure.Should().BeOfType<OidcNotAdmittedException>();
        logs.Entries.Should().Contain(x => x.Level == LogLevel.Warning && x.Message.Contains("First-run setup has not been completed"));
    }

    [Test]
    public async Task HandleTokenValidatedAsync_GuestBlockWithoutGuestSignal_WarnsOncePerProcess()
    {
        OidcAdmission.ResetWarnings();
        var logs = new LogRecorder();

        for (var i = 0; i < 2; i++)
        {
            var context = BuildContext(
                ProvisioningAs(Viewer("sub-kim")).Object,
                sub: "sub-kim",
                iss: "https://login.example.com/",
                email: null,
                name: "Kim",
                preferredUsername: "kim",
                existingRoleFromIdp: null,
                logs: logs);

            await OidcEventHandlers.HandleTokenValidatedAsync(context);

            context.Result.Should().BeNull("a token without acct or idp is still admitted as a member");
        }

        logs.Entries.Where(x => x.Message.Contains("'acct'")).Should().ContainSingle()
            .Which.Level.Should().Be(LogLevel.Warning);
        OidcAdmission.ResetWarnings();
    }

    [Test]
    public async Task HandleRemoteFailureAsync_NotAdmitted_SendsTheUserToTheNotAdmittedMessage()
    {
        var context = BuildRemoteFailureContext(new OidcNotAdmittedException());

        await OidcEventHandlers.HandleRemoteFailureAsync(context);

        context.Response.Headers.Location.ToString().Should().Be("/beacon/login?ssoError=not_admitted");
    }

    [Test]
    public async Task HandleRemoteFailureAsync_OtherFailure_KeepsTheGenericSsoError()
    {
        var context = BuildRemoteFailureContext(new InvalidOperationException("Correlation failed."));

        await OidcEventHandlers.HandleRemoteFailureAsync(context);

        context.Response.Headers.Location.ToString().Should().Be("/beacon/login?ssoError=1");
    }

    private static RemoteFailureContext BuildRemoteFailureContext(Exception failure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        var httpContext = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        var scheme = new AuthenticationScheme(
            OpenIdConnectDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme,
            typeof(DummyHandler));

        return new RemoteFailureContext(httpContext, scheme, new OpenIdConnectOptions(), failure);
    }

    private static Mock<IUserManagementService> ProvisioningAs(BeaconUserData user)
    {
        var userService = new Mock<IUserManagementService>();
        userService
            .Setup(x => x.GetOrCreateExternalUserAsync(
                user.ExternalId,
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(user);

        return userService;
    }

    private static BeaconUserData Viewer(string externalId)
    {
        return new BeaconUserData
        {
            Id = 11,
            ExternalId = externalId,
            UserName = externalId,
            IsEnabled = true,
            Roles = [new BeaconRoleData { Id = 3, Name = "Viewer", Level = 1 }]
        };
    }

    private static TokenValidatedContext BuildContext(
        IUserManagementService userService,
        string? sub,
        string? iss,
        string? email,
        string? name,
        string? preferredUsername,
        string? existingRoleFromIdp,
        string? tenantId = TenantId,
        IEnumerable<Claim>? extraClaims = null,
        Action<OidcAuthenticationOptions>? configure = null,
        string roleClaimType = ClaimTypes.Role,
        LogRecorder? logs = null)
    {
        var oidcOptions = new OidcAuthenticationOptions
        {
            Enabled = true,
            Authority = "https://login.example.com/",
            DefaultRoleName = "Viewer",
            AllowedTenants = [TenantId]
        };
        configure?.Invoke(oidcOptions);

        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton(Options.Create(oidcOptions));
        if (logs == null)
        {
            services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
            services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        }
        else
        {
            services.AddLogging(x => x.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        }

        var httpContext = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider()
        };

        var claims = new List<Claim>();
        if (!string.IsNullOrEmpty(sub))
        {
            claims.Add(new Claim("sub", sub));
        }
        if (!string.IsNullOrEmpty(iss))
        {
            claims.Add(new Claim("iss", iss));
        }
        if (!string.IsNullOrEmpty(email))
        {
            claims.Add(new Claim("email", email));
        }
        if (!string.IsNullOrEmpty(name))
        {
            claims.Add(new Claim("name", name));
        }
        if (!string.IsNullOrEmpty(preferredUsername))
        {
            claims.Add(new Claim("preferred_username", preferredUsername));
        }
        if (!string.IsNullOrEmpty(existingRoleFromIdp))
        {
            claims.Add(new Claim(ClaimTypes.Role, existingRoleFromIdp));
        }
        if (!string.IsNullOrEmpty(tenantId))
        {
            claims.Add(new Claim("tid", tenantId));
        }
        claims.AddRange(extraClaims ?? []);

        var identity = new ClaimsIdentity(claims, "oidc", ClaimTypes.Name, roleClaimType);
        var principal = new ClaimsPrincipal(identity);

        var scheme = new AuthenticationScheme(
            OpenIdConnectDefaults.AuthenticationScheme,
            OpenIdConnectDefaults.AuthenticationScheme,
            typeof(DummyHandler));

        var options = new OpenIdConnectOptions();
        options.Configuration = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectConfiguration();

        return new TokenValidatedContext(httpContext, scheme, options, principal, new AuthenticationProperties());
    }

    private sealed class DummyHandler : IAuthenticationHandler
    {
        public Task<AuthenticateResult> AuthenticateAsync() => throw new NotImplementedException();
        public Task ChallengeAsync(AuthenticationProperties? properties) => throw new NotImplementedException();
        public Task ForbidAsync(AuthenticationProperties? properties) => throw new NotImplementedException();
        public Task InitializeAsync(AuthenticationScheme scheme, HttpContext context) => throw new NotImplementedException();
    }
}
