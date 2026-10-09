using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core.Authentication;
using Beacon.Core.Authentication.Providers;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// A bearer token outside <c>/beacon/mcp</c> (and a token from the login-form JWT flow) is proof of identity only: the
/// session is the existing, enabled external Beacon user the token names, with that user's Beacon roles. A token that
/// names no such user is refused with a generic <c>invalid_token</c> answer, and token roles never reach any principal.
/// </summary>
[TestFixture]
public class RestBearerUserBindingTests
{
    private const string Issuer = "https://login.microsoftonline.com/tenant-1/v2.0";
    private const string OtherIssuer = "https://sts.windows.net/tenant-1/";
    private const string Audience = "api://beacon";
    private const string SsoClientId = "beacon-sso-client";
    private const string TenantId = "tenant-1";
    private const string AiProxyClientId = "aiproxy";
    private const string UserSub = "user-sub";
    private const string UserOid = "user-oid";

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static readonly McpCallerSubjectHasher Hasher = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));

    // aiproxy is confined by Beacon configuration to project 1 with Read scope.
    private static readonly McpCallerOptions Callers = new()
    {
        AllowedTenants = [TenantId],
        Systems =
        [
            new McpSystemCallerOptions { Name = "aiproxy", ClientId = AiProxyClientId, ProjectIds = [1], Scope = McpCallerScope.Read }
        ]
    };

    [Test]
    public async Task TokenClaimingAdmin_GetsTheBeaconUsersRoles_NotTheTokens()
    {
        var mapper = new Mock<IMcpCallerMapper>(MockBehavior.Strict);
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));
        var token = MintToken(
            UserClaims(),
            new Claim("roles", "Admin"),
            new Claim(ClaimTypes.Role, "Admin"));

        var outcome = await RunAsync("/beacon/api/admin/settings", token, mapper.Object, users.Object);

        outcome.NextInvoked.Should().BeTrue();
        outcome.User!.FindAll(ClaimTypes.Role).Select(x => x.Value).Should().Equal("Viewer");
        outcome.User.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(UserSub, "the session is the Beacon user");
        outcome.User.FindAll(McpCallerClaimTypes.AuthMethod).Select(x => x.Value).Should().Equal("jwt");
        outcome.User.FindFirst("tid").Should().BeNull("no token claim reaches the REST session");
        (await AuthorizeAsync(outcome.User, BeaconApiEndpoints.AdminPolicyName))
            .Should().BeFalse("a role in the token is never a Beacon role");
        mapper.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task UnknownSubject_IsRefusedWithAGenericInvalidTokenAnswer()
    {
        var users = Store();

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
        users.Verify(
            x => x.GetBearerUserCandidatesAsync(UserSub, Issuer, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "one lookup covers every candidate identity");
    }

    [Test]
    public async Task DisabledUser_IsRefused()
    {
        var users = Store(Row(External(UserSub, Issuer, "Admin", enabled: false)));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task ArchivedUser_IsRefused()
    {
        var users = Store(Row(External(UserSub, Issuer, "Admin"), archived: true));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task DisabledIssuerUser_IsNotRescuedByAnEnabledPreRegisteredUser()
    {
        var users = Store(
            Row(External(UserSub, Issuer, "Viewer", enabled: false)),
            Row(External(UserSub, null, "Admin")));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task DisabledPreRegisteredUser_IsNotRescuedByAnEnabledIssuerUser()
    {
        var users = Store(
            Row(External(UserSub, Issuer, "Viewer")),
            Row(External(UserSub, null, "Admin", enabled: false)));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task UserStoredUnderTheIssuer_WinsOverAPreRegisteredUser()
    {
        var users = Store(
            Row(External(UserSub, null, "Admin", id: 1)),
            Row(External(UserSub, Issuer, "Viewer", id: 2)));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        outcome.NextInvoked.Should().BeTrue();
        outcome.User!.FindAll(ClaimTypes.Role).Select(x => x.Value).Should().Equal("Viewer");
    }

    [Test]
    public async Task UserProvisionedForMcp_GetsNoRestSession()
    {
        // MCP provisioning stores users by (oid, entra:{tid}); a REST bearer token never reaches them.
        var users = Store(Row(External(UserOid, $"entra:{TenantId}", "Editor")));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
        users.Verify(
            x => x.GetBearerUserCandidatesAsync(UserOid, It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Test]
    public async Task PreRegisteredUserWithoutAProvider_IsFoundBySubject_WhenOneIssuerIsConfigured()
    {
        var users = Store(Row(External(UserSub, null, "Viewer")));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        outcome.NextInvoked.Should().BeTrue();
        outcome.User!.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be(UserSub);
    }

    [Test]
    public async Task PreRegisteredUserWithoutAProvider_IsNotFound_WhenSeveralIssuersAreConfigured()
    {
        var users = Store(Row(External(UserSub, null, "Viewer")));
        var jwtOptions = CreateJwtOptions();
        jwtOptions.Validation.ValidIssuers = [OtherIssuer];

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims()),
            CreateMapper(),
            users.Object,
            jwtOptions: jwtOptions);

        AssertRefused(outcome);
        users.Verify(
            x => x.GetBearerUserCandidatesAsync(UserSub, Issuer, false, It.IsAny<CancellationToken>()),
            Times.Once,
            "with several issuers a pre-registered user must name its identity provider");
    }

    [Test]
    public async Task InternalUserWhoseExternalIdIsTheTokenSubject_IsRefused()
    {
        var internalUser = External(UserSub, null, "Admin");
        internalUser.IsInternalUser = true;
        var users = Store(Row(internalUser));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task SuperAdminWhoseExternalIdIsTheTokenSubject_IsRefused()
    {
        var superAdmin = External(UserSub, Issuer, "Admin");
        superAdmin.IsSuperAdmin = true;
        var users = Store(Row(superAdmin));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task ExternalIdDifferingOnlyInCase_IsRefused()
    {
        // The double matches case-insensitively, like a SQL Server collation; the binding re-checks ordinally.
        var users = Store(Row(External(UserSub.ToUpperInvariant(), Issuer, "Admin")));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task ConfiguredUserIdClaim_NamesTheUser()
    {
        var users = Store(Row(External("employee-7", Issuer, "Editor")));
        var jwtOptions = CreateJwtOptions();
        jwtOptions.ClaimsMapping.UserIdClaim = "employee_id";

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims(), new Claim("employee_id", "employee-7")),
            CreateMapper(),
            users.Object,
            jwtOptions: jwtOptions);

        outcome.NextInvoked.Should().BeTrue();
        outcome.User!.FindAll(ClaimTypes.Role).Select(x => x.Value).Should().Equal("Editor");
    }

    [Test]
    public async Task ConfiguredUserIdClaimMissing_IsRefused_InsteadOfFallingBackToSub()
    {
        var users = Store(Row(External(UserSub, Issuer, "Admin")));
        var jwtOptions = CreateJwtOptions();
        jwtOptions.ClaimsMapping.UserIdClaim = "employee_id";

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims()),
            CreateMapper(),
            users.Object,
            jwtOptions: jwtOptions);

        AssertRefused(outcome);
        users.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task SubjectUnderAnotherSpelling_IsIgnored()
    {
        var users = Store(Row(External(UserSub, Issuer, "Admin")));
        var claims = UserClaims()
            .Where(x => x.Type != "sub")
            .Append(new Claim("SUB", UserSub));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(claims), CreateMapper(), users.Object);

        AssertRefused(outcome);
        users.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task IdToken_IsRefusedOnRest_EvenForAKnownUser()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims(), new Claim("nonce", "n-1")),
            CreateMapper(),
            users.Object);

        AssertRefused(outcome);
        users.Invocations.Should().BeEmpty();
    }

    [Test]
    public async Task TokenForTheSsoClientWithoutScopes_IsTreatedAsAnIdToken()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));
        var claims = UserClaims().Where(x => x.Type != "scp");

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(claims, audience: SsoClientId),
            CreateMapper(),
            users.Object,
            oidc: Sso());

        AssertRefused(outcome);
    }

    [Test]
    public async Task AccessTokenForTheSsoClient_WithAScope_IsAccepted()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims(), audience: SsoClientId),
            CreateMapper(),
            users.Object,
            oidc: Sso());

        outcome.NextInvoked.Should().BeTrue();
    }

    [Test]
    public async Task SsoIssuedToken_FromAnotherTenant_IsRefused()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));
        var claims = UserClaims()
            .Where(x => x.Type != "tid")
            .Append(new Claim("tid", "tenant-2"));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(claims), CreateMapper(), users.Object, oidc: Sso());

        AssertRefused(outcome);
        users.Invocations.Should().BeEmpty("admission runs before any lookup");
    }

    [Test]
    public async Task SsoIssuedToken_ForAGuest_IsRefused()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));

        var outcome = await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims(), new Claim("acct", "1")),
            CreateMapper(),
            users.Object,
            oidc: Sso());

        AssertRefused(outcome);
    }

    [Test]
    public async Task SsoIssuedToken_WithoutARequiredAppRole_IsRefused()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));
        var sso = Sso();
        sso.RequiredRoles = ["Beacon.User"];

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object, oidc: sso);

        AssertRefused(outcome);
    }

    [Test]
    public async Task SsoIssuedToken_ForAnAdmittedMember_IsBound()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));

        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users.Object, oidc: Sso());

        outcome.NextInvoked.Should().BeTrue();
        outcome.User!.FindAll(ClaimTypes.Role).Select(x => x.Value).Should().Equal("Viewer");
    }

    [Test]
    public async Task WithoutUserManagement_RestBearerTokensAreRefused()
    {
        var outcome = await RunAsync("/beacon/api/projects", MintToken(UserClaims()), CreateMapper(), users: null);

        AssertRefused(outcome);
    }

    [Test]
    public async Task Refusal_IsLoggedWithAReasonAndASubjectHash_NeverTheSubject_AndWarnsOncePerMinute()
    {
        var logs = new LogRecorder();
        var users = Store();
        var middleware = CreateMiddleware(CreateJwtOptions(), logs.For<JwtBearerAuthMiddleware>());

        await InvokeAsync(middleware, "/beacon/api/projects", MintToken(UserClaims()), users.Object);
        await InvokeAsync(middleware, "/beacon/api/projects", MintToken(UserClaims()), users.Object);

        var refusals = logs.Entries
            .Where(x => x.Message.StartsWith("Bearer token refused", StringComparison.Ordinal))
            .ToList();
        refusals.Select(x => x.Level).Should().Equal(LogLevel.Warning, LogLevel.Debug);
        refusals[0].Message.Should().Contain(nameof(BearerRefusal.NoBeaconUser))
            .And.Contain(TenantId)
            .And.Contain(Issuer)
            .And.Contain(SubjectFingerprint.Of(UserSub));
        logs.Entries.Should().NotContain(x => x.FullText.Contains(UserSub, StringComparison.Ordinal));
    }

    [Test]
    public async Task SystemConfinedToProject1Read_OnMcp_IsRefusedOnRest()
    {
        var token = MintToken(AppOnlyClaims());
        var users = Store();

        // On /beacon/mcp the configured confinement applies: Read scope, project 1 only.
        var mcp = await RunAsync("/beacon/mcp", token, CreateMapper(), users.Object);
        (await AuthorizeAsync(mcp.User!, BeaconApiEndpoints.ExecuteScopePolicyName)).Should().BeFalse();
        mcp.User!.FindAll("scope").Select(x => x.Value).Should().Equal("Read");
        mcp.User.FindAll("allowed_projects").Select(x => x.Value).Should().Equal("[1]");
        ProjectAccess.IsAllowed(mcp.User, 7).Should().BeFalse();

        // The same token on a REST route names no Beacon user: refused before any endpoint, the playground included.
        var rest = await RunAsync("/beacon/api/mcp/tools/run", token, CreateMapper(), users.Object);

        AssertRefused(rest);
    }

    [Test]
    public async Task McpRoute_TokenAuthorizationClaims_NeverReachThePrincipal()
    {
        var token = MintToken(
            AppOnlyClaims(),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(BeaconClaims.Role, "Admin"),
            new Claim("BEACON:ROLE", "Admin"),
            new Claim("roles", "Admin"),
            new Claim("role", "Admin"),
            new Claim("groups", "admins"),
            new Claim("wids", "62e90394-69f5-4237-9190-012177145e10"),
            new Claim(BeaconClaims.UserId, "1"),
            new Claim("Beacon:User_Id", "1"),
            new Claim(BeaconClaims.Permission, "write"));

        var outcome = await RunAsync("/beacon/mcp", token, CreateMapper(), users: null);

        var types = outcome.User!.Claims
            .Select(x => x.Type)
            .ToList();
        types.Should().NotContain(x => x.StartsWith("beacon:", StringComparison.OrdinalIgnoreCase));
        types.Should().NotContain(["roles", "role", "groups", "wids", ClaimTypes.Role]);
        outcome.User.IsInRole("Admin").Should().BeFalse();
        (await AuthorizeAsync(outcome.User, BeaconApiEndpoints.AdminPolicyName)).Should().BeFalse();
    }

    [Test]
    public async Task McpRoute_TokenSuppliedBeaconUserId_DoesNotReachTheUserContext()
    {
        var token = MintToken(AppOnlyClaims(), new Claim(BeaconClaims.UserId, "1"), new Claim(BeaconClaims.UserName, "admin"));

        var outcome = await RunAsync("/beacon/mcp", token, CreateMapper(), users: null);

        outcome.Context.User = outcome.User!;
        var userContext = new HttpContextUserContext(new HttpContextAccessor { HttpContext = outcome.Context });
        userContext.UserId.Should().NotBe("1");
        userContext.UserName.Should().NotBe("admin");
    }

    [Test]
    public async Task NonApiRequest_WithAnUnknownSubject_ContinuesAnonymously()
    {
        var users = Store();

        var outcome = await RunAsync(
            "/projects",
            MintToken(UserClaims()),
            CreateMapper(),
            users.Object,
            accept: "text/html");

        outcome.NextInvoked.Should().BeTrue();
        outcome.User!.Identity!.IsAuthenticated.Should().BeFalse();
        outcome.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task MalformedToken_IsAGeneric401_NotAServerError()
    {
        var outcome = await RunAsync(
            "/beacon/api/projects",
            "not-a-jwt",
            CreateMapper(),
            Store().Object);

        AssertRefused(outcome);
    }

    [Test]
    public async Task UserLookup_IsCancelledWithTheRequest()
    {
        using var aborted = new CancellationTokenSource();
        var users = Store(Row(External(UserSub, Issuer, "Viewer")));

        await RunAsync(
            "/beacon/api/projects",
            MintToken(UserClaims()),
            CreateMapper(),
            users.Object,
            requestAborted: aborted.Token);

        users.Verify(
            x => x.GetBearerUserCandidatesAsync(UserSub, Issuer, true, aborted.Token),
            Times.Once);
    }

    [Test]
    public async Task LoginFormJwt_TokenClaimingAdmin_SignsInWithTheBeaconUsersRoles()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer", id: 42)));
        var provider = CreateLoginProvider(MintToken(UserClaims(), new Claim("roles", "Admin")), users.Object);

        var result = await provider.AuthenticateAsync("ana", "secret");

        result.Success.Should().BeTrue();
        result.BeaconUserId.Should().Be(42);
        result.User!.UserId.Should().Be(UserSub);
        result.User.Roles.Should().Equal("Viewer");
        result.User.Claims.Should().BeEmpty("no token claim is carried into the cookie session");
        users.Verify(x => x.UpdateLastLoginAsync(42, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task LoginFormJwt_TokenForAnUnknownUser_IsRefused()
    {
        var users = Store();
        var provider = CreateLoginProvider(MintToken(UserClaims()), users.Object);

        var result = await provider.AuthenticateAsync("ana", "secret");

        result.Success.Should().BeFalse();
        result.User.Should().BeNull();
        result.ErrorMessage.Should().Be("Invalid username or password.");
    }

    [Test]
    public async Task LoginFormJwt_DisabledUser_IsRefusedWithTheSameAnswer()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer", enabled: false)));
        var provider = CreateLoginProvider(MintToken(UserClaims()), users.Object);

        var result = await provider.AuthenticateAsync("ana", "secret");

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("Invalid username or password.");
        users.Verify(x => x.UpdateLastLoginAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task LoginFormJwt_WithoutUserManagement_IsRefused()
    {
        var provider = CreateLoginProvider(MintToken(UserClaims()), userService: null);

        var result = await provider.AuthenticateAsync("ana", "secret");

        result.Success.Should().BeFalse();
        result.User.Should().BeNull();
        result.ErrorMessage.Should().Be("Invalid username or password.");
    }

    [TestCase(HttpStatusCode.Unauthorized)]
    [TestCase(HttpStatusCode.InternalServerError)]
    public async Task LoginFormJwt_EveryFailure_AnswersTheSame_AndNeverLogsTheUserName(HttpStatusCode status)
    {
        var logs = new LogRecorder();
        var provider = new JwtExternalApiAuthenticationProvider(
            new HttpClient(new StatusHandler(status)),
            CreateJwtOptions(),
            logs.For<JwtExternalApiAuthenticationProvider>(),
            Store().Object);

        var result = await provider.AuthenticateAsync("ana.analyst", "secret");

        result.ErrorMessage.Should().Be("Invalid username or password.");
        logs.Entries.Should().NotBeEmpty();
        logs.Entries.Should().NotContain(x => x.FullText.Contains("ana.analyst", StringComparison.Ordinal));
    }

    [Test]
    public async Task LoginFormJwt_UnreachableLoginApi_AnswersTheSame_AndNeverLogsTheUserName()
    {
        var logs = new LogRecorder();
        var provider = new JwtExternalApiAuthenticationProvider(
            new HttpClient(new ThrowingHandler()),
            CreateJwtOptions(),
            logs.For<JwtExternalApiAuthenticationProvider>(),
            Store().Object);

        var result = await provider.AuthenticateAsync("ana.analyst", "secret");

        result.ErrorMessage.Should().Be("Invalid username or password.");
        logs.Entries.Should().NotContain(x => x.FullText.Contains("ana.analyst", StringComparison.Ordinal));
    }

    [Test]
    public async Task HybridLogin_ReReadsTheBoundUserById_NeverAnotherAccountWithTheSameExternalId()
    {
        var users = Store(Row(External(UserSub, Issuer, "Viewer", id: 42)));
        users
            .Setup(x => x.AuthenticateInternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(AuthenticationResult.Failed("Invalid username or password."));
        users
            .Setup(x => x.GetUserByIdAsync(42, It.IsAny<CancellationToken>()))
            .ReturnsAsync(External(UserSub, Issuer, "Viewer", id: 42));
        var hybrid = new HybridAuthenticationProvider(
            users.Object,
            CreateLoginProvider(MintToken(UserClaims()), users.Object));

        var result = await hybrid.AuthenticateAsync("ana", "secret");

        result.Success.Should().BeTrue();
        result.BeaconUserId.Should().Be(42);
        result.User!.Roles.Should().Equal("Viewer");
        users.Verify(x => x.GetUserByExternalIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        users.Verify(x => x.UpdateLastLoginAsync(42, It.IsAny<CancellationToken>()), Times.Once, "the login is recorded once");
    }

    private static void AssertRefused(Outcome outcome)
    {
        outcome.NextInvoked.Should().BeFalse("a refused token never reaches an endpoint");
        outcome.Context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        outcome.Context.Response.Headers.WWWAuthenticate.ToString().Should().Be("Bearer error=\"invalid_token\"");
        outcome.Body.Should().Contain("invalid_token");
        outcome.Body.Should().NotContainAny(["user", "issuer", "signature", "expired"], "the reason stays in the server log");
    }

    private static (BeaconUserData User, bool Archived) Row(BeaconUserData user, bool archived = false) => (user, archived);

    /// <summary>
    /// A user store double over <paramref name="rows"/>: it answers the bearer lookup like the database would, except
    /// that it matches external ids case-insensitively (like a SQL Server collation) and returns internal users and
    /// super admins too, so the binding's own checks are what the tests observe.
    /// </summary>
    private static Mock<IUserManagementService> Store(params (BeaconUserData User, bool Archived)[] rows)
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.GetBearerUserCandidatesAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string externalId, string identityProvider, bool includeWithoutIdentityProvider, CancellationToken _) => rows
                .Where(x => string.Equals(x.User.ExternalId, externalId, StringComparison.OrdinalIgnoreCase))
                .Where(x => x.User.IdentityProvider == identityProvider
                    || (includeWithoutIdentityProvider && x.User.IdentityProvider == null))
                .Select(x => new BearerUserCandidate { User = x.User, IsArchived = x.Archived })
                .ToList());

        return users;
    }

    private static BeaconUserData External(string externalId, string? identityProvider, string role, bool enabled = true, int id = 42)
    {
        return new BeaconUserData
        {
            Id = id,
            ExternalId = externalId,
            IdentityProvider = identityProvider,
            UserName = "ana",
            DisplayName = "Ana Analyst",
            IsEnabled = enabled,
            Roles = [new BeaconRoleData { Id = 1, Name = role, Level = 1 }]
        };
    }

    private static OidcAuthenticationOptions Sso()
    {
        return new OidcAuthenticationOptions
        {
            Enabled = true,
            Authority = Issuer,
            ClientId = SsoClientId,
            ClientSecret = "unused",
            AllowedTenants = [TenantId]
        };
    }

    private static ConfiguredMcpCallerMapper CreateMapper()
    {
        return new ConfiguredMcpCallerMapper(
            Options.Create(Callers),
            new MemoryCache(new MemoryCacheOptions()),
            Hasher,
            NullLogger<ConfiguredMcpCallerMapper>.Instance,
            null,
            CreateJwtOptions());
    }

    private static JwtAuthenticationOptions CreateJwtOptions()
    {
        return new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            ExternalLoginEndpoint = "https://auth.example.test/login",
            Validation = new JwtValidationOptions
            {
                SigningKey = SigningKey,
                ValidIssuer = Issuer,
                ValidAudiences = [Audience, SsoClientId]
            }
        };
    }

    private static JwtExternalApiAuthenticationProvider CreateLoginProvider(string issuedToken, IUserManagementService? userService)
    {
        return new JwtExternalApiAuthenticationProvider(
            new HttpClient(new TokenIssuingHandler(issuedToken)),
            CreateJwtOptions(),
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance,
            userService);
    }

    private static JwtBearerAuthMiddleware CreateMiddleware(
        JwtAuthenticationOptions jwtOptions,
        ILogger<JwtBearerAuthMiddleware> logger,
        Action<HttpContext>? onNext = null)
    {
        return new JwtBearerAuthMiddleware(
            x =>
            {
                onNext?.Invoke(x);
                return Task.CompletedTask;
            },
            jwtOptions,
            logger);
    }

    private static async Task<Outcome> RunAsync(
        string path,
        string token,
        IMcpCallerMapper mapper,
        IUserManagementService? users,
        string? accept = null,
        CancellationToken requestAborted = default,
        JwtAuthenticationOptions? jwtOptions = null,
        OidcAuthenticationOptions? oidc = null)
    {
        ClaimsPrincipal? seen = null;
        var nextInvoked = false;
        var middleware = CreateMiddleware(
            jwtOptions ?? CreateJwtOptions(),
            NullLogger<JwtBearerAuthMiddleware>.Instance,
            x =>
            {
                seen = x.User;
                nextInvoked = true;
            });

        var (context, body) = await InvokeAsync(middleware, path, token, users, mapper, accept, requestAborted, jwtOptions, oidc);

        return new Outcome(context, seen, nextInvoked, body);
    }

    private static async Task<(HttpContext Context, string Body)> InvokeAsync(
        JwtBearerAuthMiddleware middleware,
        string path,
        string token,
        IUserManagementService? users,
        IMcpCallerMapper? mapper = null,
        string? accept = null,
        CancellationToken requestAborted = default,
        JwtAuthenticationOptions? jwtOptions = null,
        OidcAuthenticationOptions? oidc = null)
    {
        var provider = new JwtExternalApiAuthenticationProvider(
            new HttpClient(),
            jwtOptions ?? CreateJwtOptions(),
            NullLogger<JwtExternalApiAuthenticationProvider>.Instance);

        var services = new ServiceCollection();
        if (users != null)
        {
            services.AddSingleton(users);
        }

        if (oidc != null)
        {
            services.AddSingleton(Options.Create(oidc));
        }

        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            RequestAborted = requestAborted
        };
        context.Request.Path = path;
        context.Request.Headers.Authorization = $"Bearer {token}";
        if (accept != null)
        {
            context.Request.Headers.Accept = accept;
        }

        using var body = new MemoryStream();
        context.Response.Body = body;

        await middleware.InvokeAsync(context, provider, mapper ?? CreateMapper());

        return (context, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static string MintToken(IEnumerable<Claim> claims, params Claim[] extra)
    {
        return MintToken(claims.Concat(extra), Audience);
    }

    private static string MintToken(IEnumerable<Claim> claims, string audience)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            audience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: credentials);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static List<Claim> UserClaims()
    {
        return
        [
            new Claim("sub", UserSub),
            new Claim("oid", UserOid),
            new Claim("tid", TenantId),
            new Claim("azp", AiProxyClientId),
            new Claim("scp", "Mcp.Access")
        ];
    }

    private static List<Claim> AppOnlyClaims()
    {
        return
        [
            new Claim("sub", "sp-oid"),
            new Claim("oid", "sp-oid"),
            new Claim("tid", TenantId),
            new Claim("azp", AiProxyClientId),
            new Claim("idtyp", "app")
        ];
    }

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal user, string policyName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiAuthorization();
        using var provider = services.BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        var result = await authorization.AuthorizeAsync(user, resource: null, policyName);

        return result.Succeeded;
    }

    private sealed record Outcome(HttpContext Context, ClaimsPrincipal? User, bool NextInvoked, string Body);

    /// <summary>Stands in for the external login API: answers every request with the given token.</summary>
    private sealed class TokenIssuingHandler(string token) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { token })
            });
        }
    }

    private sealed class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("connection refused");
        }
    }
}
