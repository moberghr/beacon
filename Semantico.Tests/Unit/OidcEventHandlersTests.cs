using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Semantico.Core;
using Semantico.Core.Authentication;
using Semantico.Core.Authorization;
using Semantico.Core.Models;
using Semantico.Core.Models.UserManagement;
using Semantico.Core.Services;
using Semantico.UI.Authentication;

namespace Semantico.Tests.Unit;

[TestFixture]
public class OidcEventHandlersTests
{
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
            .ReturnsAsync(new SemanticoUserData
            {
                Id = 42,
                ExternalId = "sub-alice",
                IdentityProvider = "https://login.example.com/",
                UserName = "alice@example.com",
                Email = "alice@example.com",
                DisplayName = "Alice Example",
                IsInternalUser = false,
                IsEnabled = true,
                Roles = new List<SemanticoRoleData>
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

        identity.FindFirst(SemanticoClaims.UserId)!.Value.Should().Be("sub-alice");
        identity.FindFirst(SemanticoClaims.UserName)!.Value.Should().Be("alice@example.com");
        var semRoles = identity.FindAll(SemanticoClaims.Role).Select(c => c.Value).ToList();
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
            .ThrowsAsync(new SemanticoException("This account has been disabled."));

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
        context.Result!.Failure!.Message.Should().Contain("disabled");
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
            .ReturnsAsync(new SemanticoUserData
            {
                Id = 1,
                ExternalId = "sub-c",
                UserName = "carol@example.com",
                Email = "carol@example.com",
                Roles = new List<SemanticoRoleData>()
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

    private static TokenValidatedContext BuildContext(
        IUserManagementService userService,
        string? sub,
        string? iss,
        string? email,
        string? name,
        string? preferredUsername,
        string? existingRoleFromIdp)
    {
        var services = new ServiceCollection();
        services.AddSingleton(userService);
        services.AddSingleton(Options.Create(new OidcAuthenticationOptions
        {
            Enabled = true,
            Authority = "https://login.example.com/",
            DefaultRoleName = "Viewer"
        }));
        services.AddSingleton<Microsoft.Extensions.Logging.ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(NullLogger<>));

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

        var identity = new ClaimsIdentity(claims, "oidc");
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
