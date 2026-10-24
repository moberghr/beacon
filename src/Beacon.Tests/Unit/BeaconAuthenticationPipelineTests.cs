using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Api.Hubs;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// What a caller of Beacon's routes sees from <c>UseBeaconAuthentication</c>, whatever middlewares or handlers implement
/// it: the answer to an API key that is not accepted (also on an anonymous route), a revoked key or one whose owner is
/// inactive, the body of every 401 by route class, the MCP caller a bearer token is mapped to, the cookie session a bad
/// bearer header beside it does not hide on a page, an API key that never reaches an Admin route nor passes through the
/// host's claims transformation, and the realtime hub with a live and with an expired session. A host with the login
/// form, user management, an API-key store, bearer tokens and single sign-on's claims transformation; no database
/// (§4.7). Covered elsewhere and not repeated here: the first cookie request after sign-in (<c>SampleHostPipelineTests</c>),
/// the anonymous discovery documents and the <c>/WARP</c> redirect (<c>SampleHostPipelineTests</c>), a Beacon credential on
/// a host route and the cookie as the host's default scheme (<c>BeaconAuthenticationScopeTests</c>), and a hub connection
/// closed when the session's next check is due (<c>RealtimeConnectionExpiryTests</c>).
/// </summary>
[TestFixture]
public class BeaconAuthenticationPipelineTests
{
    private const string ReadKey = "sk-sem_pipeline-read-key";
    private const string AdminOwnerKey = "sk-sem_pipeline-admin-owner-key";
    private const string InactiveOwnerKey = "sk-sem_pipeline-inactive-owner-key";
    private const string NoScopeKey = "sk-sem_pipeline-no-scope-key";
    private const string UnknownKey = "sk-sem_pipeline-unknown-key";
    private const string Issuer = "https://idp.example.test";
    private const string Audience = "api://beacon";
    private const string RestSubject = "rest-sub";
    private const string McpSubject = "mcp-sub";
    private const string UnmappedSubject = "unmapped-sub";
    private const string TransformedClaim = "test:transformed";
    private const string InvalidKeyDetail = "Invalid or expired API key.";

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    [Test]
    public async Task UnacceptedApiKey_OnAnAnonymousBeaconRoute_Is401ProblemDetails()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/beacon/api/anonymous", bearer: UnknownKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a presented key that is not accepted is refused, also where no sign-in is needed");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(401);
        problem.GetProperty("title").GetString().Should().Be("Unauthorized");
        problem.GetProperty("detail").GetString().Should().Be(InvalidKeyDetail);
    }

    [Test]
    public async Task UnacceptedApiKey_OnAnAnonymousHostRoute_IsLeftToTheHost()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/reports", bearer: UnknownKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "Beacon neither accepts nor refuses a credential on the host's own route");
        (await response.Content.ReadAsStringAsync()).Should().Be("reports");
    }

    [TestCase(UnknownKey, InvalidKeyDetail)]
    [TestCase(NoScopeKey, InvalidKeyDetail)]
    [TestCase(InactiveOwnerKey, "This API key is inactive: its owner has not signed in to Beacon for 90 days. The key works again after the owner signs in.")]
    public async Task RevokedOrRefusedApiKey_Is401_WithItsReason(string key, string detail)
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/beacon/api/whoami", bearer: key);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString().Should().Be(detail);
    }

    [Test]
    public async Task AcceptedApiKey_ReachesAnAuthorizedRoute_AsTheKey()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/beacon/api/whoami", bearer: ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(McpCallerClaimTypes.ApiKeyAuthenticationType);
    }

    [Test]
    public async Task ApiKey_OfASuperAdmin_NeverReachesAnAdminRoute_WhileTheOwnersSessionDoes()
    {
        await using var host = await PipelineHost.StartAsync();
        var cookie = await host.SignInAsync();

        var asKey = await host.SendAsync("/beacon/api/admin", bearer: AdminOwnerKey);
        var asSession = await host.SendAsync("/beacon/api/admin", cookie: cookie);

        asKey.StatusCode.Should().Be(HttpStatusCode.Forbidden, "an API key never carries the Admin standing, whoever owns it");
        asSession.StatusCode.Should().Be(HttpStatusCode.OK, "the same user's session is an Admin through the host's claims transformation");
    }

    [Test]
    public async Task BearerCredentials_AreNotPassedThroughTheHostsClaimsTransformation_TheSessionIs()
    {
        await using var host = await PipelineHost.StartAsync();
        var cookie = await host.SignInAsync();

        var key = await host.ClaimsAsync("/beacon/api/claims", bearer: AdminOwnerKey);
        var rest = await host.ClaimsAsync("/beacon/api/claims", bearer: MintToken(RestSubject));
        var session = await host.ClaimsAsync("/beacon/api/claims", cookie: cookie);

        key.Should().NotContainKey(TransformedClaim);
        key.Should().NotContainKey(ClaimTypes.Role);
        rest.Should().NotContainKey(TransformedClaim);
        session.Should().ContainKey(TransformedClaim);
    }

    [Test]
    public async Task Jwt_OnMcp_IsTheCallerTheMapperDecides_WithNoTokenSuppliedGrant()
    {
        await using var host = await PipelineHost.StartAsync();

        var claims = await host.ClaimsAsync(
            "/beacon/mcp",
            bearer: MintToken(McpSubject, new Claim("roles", "Admin"), new Claim(McpCallerClaimTypes.AllowedProjects, "[1,2,3]")));

        claims[McpCallerClaimTypes.AuthMethod].Should().Equal(McpCallerClaimTypes.McpCallerAuthMethod);
        claims[McpCallerClaimTypes.Scope].Should().Equal(nameof(McpCallerScope.Execute));
        claims[McpCallerClaimTypes.AllowedProjects].Should().Equal("[5]");
        claims[McpCallerClaimTypes.CallerKind].Should().Equal(nameof(McpCallerKind.User));
        claims[McpCallerClaimTypes.CallerHash].Should().Equal("caller-hash");
        claims[ClaimTypes.NameIdentifier].Should().Equal("42");
        claims["authentication_type"].Should().Equal("Bearer");
        claims["mcp_feature"].Should().Equal("present");
        claims.Should().NotContainKey("roles");
        claims.Should().NotContainKey(ClaimTypes.Role);
    }

    [Test]
    public async Task Jwt_OnMcp_UnmappedCaller_StaysAuthenticated_WithoutScopeProjectsOrIdentifier()
    {
        await using var host = await PipelineHost.StartAsync();

        var claims = await host.ClaimsAsync("/beacon/mcp", bearer: MintToken(UnmappedSubject));

        claims[McpCallerClaimTypes.AuthMethod].Should().Equal(McpCallerClaimTypes.McpCallerAuthMethod);
        claims.Should().NotContainKey(McpCallerClaimTypes.Scope);
        claims.Should().NotContainKey(McpCallerClaimTypes.AllowedProjects);
        claims.Should().NotContainKey(ClaimTypes.NameIdentifier);
    }

    [Test]
    public async Task Jwt_OnARestRoute_IsTheBoundBeaconUser()
    {
        await using var host = await PipelineHost.StartAsync();

        var claims = await host.ClaimsAsync("/beacon/api/claims", bearer: MintToken(RestSubject));

        claims[McpCallerClaimTypes.AuthMethod].Should().Equal(McpCallerClaimTypes.JwtAuthMethod);
        claims[ClaimTypes.NameIdentifier].Should().Equal(RestSubject);
        claims["authentication_type"].Should().Equal("Bearer");
        claims["mcp_feature"].Should().Equal("absent");
    }

    [TestCase("/beacon/api/whoami")]
    [TestCase("/beacon/mcp")]
    public async Task Anonymous_OnAnApiRoute_Is401ProblemDetails_NotALoginRedirect(string path)
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(401);
        problem.GetProperty("title").GetString().Should().Be("Unauthorized");
    }

    [Test]
    public async Task Anonymous_OnAUiPage_IsRedirectedToTheLoginPage()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/beacon/home");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/beacon/login");
    }

    [Test]
    public async Task Anonymous_OnTheListedWarpDashboard_IsChallengedWithTheLoginRedirect()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/warp/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().StartWith("http://localhost/beacon/login?ReturnUrl=");
    }

    [Test]
    public async Task UnacceptedApiKey_OnTheListedWarpDashboard_Is401ProblemDetails()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/warp/jobs", bearer: UnknownKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [TestCase("/beacon/api/whoami")]
    [TestCase("/beacon/mcp")]
    public async Task UnacceptedJwt_OnAnApiRoute_Is401WithTheInvalidTokenChallenge(string path)
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync(path, bearer: "not-a-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer error=\"invalid_token\"");
        (await response.Content.ReadAsStringAsync()).Should().Contain("invalid_token");
    }

    [Test]
    public async Task UnacceptedJwt_OnAUiPage_ContinuesAnonymously_ToTheLoginRedirect()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync("/beacon/home", bearer: "not-a-token");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/beacon/login");
    }

    [Test]
    public async Task UnacceptedJwt_OnAUiPage_LeavesTheSessionCookieBesideItToAuthenticate()
    {
        await using var host = await PipelineHost.StartAsync();
        var cookie = await host.SignInAsync();

        var response = await host.SendAsync("/beacon/home", bearer: "not-a-token", cookie: cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("spa");
    }

    [Test]
    public async Task Hub_AdmitsALiveSession_AndRefusesItOnceTheSessionHasExpired()
    {
        await using var host = await PipelineHost.StartAsync();
        var cookie = await host.SignInAsync();

        var live = await host.NegotiateAsync(cookie: cookie);
        host.Time.Advance(TimeSpan.FromHours(13));
        var expired = await host.NegotiateAsync(cookie: cookie);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        expired.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session's absolute lifetime has passed");
        expired.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task Hub_RefusesAnApiKey()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.NegotiateAsync(bearer: ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the hub admits sessions only");
    }

    // An access token (typ at+jwt) from the configured issuer, for the given subject.
    private static string MintToken(string subject, params Claim[] extra)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        var header = new JwtHeader(credentials);
        header[JwtHeaderParameterNames.Typ] = "at+jwt";
        var payload = new JwtPayload(
            Issuer,
            Audience,
            [new Claim("sub", subject), .. extra],
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10));

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
    }

    private sealed class PipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineHost(WebApplication app, FakeTimeProvider time)
        {
            _app = app;
            Time = time;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public FakeTimeProvider Time { get; }

        public static async Task<PipelineHost> StartAsync()
        {
            var configuration = new BeaconConfiguration();
            configuration.Authentication.EnableLoginForm = true;
            configuration.UserManagement.Enabled = true;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:Authentication:AdditionalPaths:0"] = "/warp"
            });

            var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
            builder.Services.AddOptions<AuthenticationPathOptions>()
                .Bind(builder.Configuration.GetSection(AuthenticationPathOptions.SectionName));
            builder.Services.AddSingleton<AuthenticationPathPrefixes>();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(UserStore().Object);
            builder.Services.AddSingleton<TimeProvider>(time);
            builder.Services.AddSingleton<IUserSessionStore>(new SessionStore());
            builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 0, AbsoluteLifetimeHours = 12 }));
            builder.Services.AddSingleton(Options.Create(new RoleMappingOptions()));
            builder.Services.AddSingleton(ApiKeys().Object);
            builder.Services.AddSingleton(Authorization().Object);
            builder.Services.AddSingleton(CallerMapper().Object);
            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBeaconCookieAuthentication("/beacon");
            builder.Services.AddBeaconJwtAuthentication(x =>
            {
                x.EnableBearerAuthentication = true;
                x.Validation.SigningKey = SigningKey;
                x.Validation.ValidIssuer = Issuer;
                x.Validation.ValidAudience = Audience;
            });
            builder.Services.AddBeaconHostInfrastructure<AdminRoleTransformation>();
            builder.Services.AddBeaconApiAuthorization();
            builder.Services.AddSignalR();

            var app = builder.Build();
            app.UseRouting();
            app.UseBeaconAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<LoginFormAuthMiddleware>(configuration, "/beacon");

            app.MapGet("/beacon/api/anonymous", (HttpContext context) => $"anonymous:{context.User.Identity?.IsAuthenticated == true}")
                .AllowAnonymous();
            app.MapGet("/beacon/api/whoami", (HttpContext context) => context.User.Identity?.AuthenticationType ?? string.Empty)
                .RequireAuthorization(BeaconApiEndpoints.AuthPolicyName);
            app.MapGet("/beacon/api/claims", Claims)
                .RequireAuthorization(BeaconApiEndpoints.AuthPolicyName);
            app.MapGet("/beacon/api/admin", () => "admin")
                .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);
            app.MapGet("/beacon/mcp", Claims)
                .RequireAuthorization();
            app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                    CookieAuthenticationDefaults.AuthenticationScheme));
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            }).AllowAnonymous();
            app.MapHub<BeaconHub>(BeaconApiEndpoints.HubPath, x => x.CloseOnAuthenticationExpiration = true)
                .RequireAuthorization(BeaconApiEndpoints.AuthPolicyName)
                .RequireAuthorization(BeaconApiEndpoints.HubAccessPolicy);
            app.MapGet("/beacon/{**page}", () => "spa");
            app.MapGet("/warp/{**page}", () => "warp")
                .RequireAuthorization();
            app.MapGet("/reports", () => "reports");

            await app.StartAsync();

            return new PipelineHost(app, time);
        }

        public async Task<string> SignInAsync()
        {
            var response = await Client.GetAsync("/beacon/api/test/sign-in");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return response.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal))
                .Split(';')[0];
        }

        public async Task<HttpResponseMessage> SendAsync(string path, string? bearer = null, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            AddCredentials(request, bearer, cookie);

            return await Client.SendAsync(request);
        }

        public async Task<HttpResponseMessage> NegotiateAsync(string? bearer = null, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BeaconApiEndpoints.HubPath}/negotiate?negotiateVersion=1");
            AddCredentials(request, bearer, cookie);

            return await Client.SendAsync(request);
        }

        public async Task<Dictionary<string, string[]>> ClaimsAsync(string path, string? bearer = null, string? cookie = null)
        {
            var response = await SendAsync(path, bearer, cookie);
            response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

            return (await response.Content.ReadFromJsonAsync<Dictionary<string, string[]>>())!;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }

        private static void AddCredentials(HttpRequestMessage request, string? bearer, string? cookie)
        {
            if (bearer != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
            }

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }
        }

        // The caller's claims by type, with the identity's authentication type and whether the MCP caller feature is set.
        private static Dictionary<string, string[]> Claims(HttpContext context)
        {
            var claims = context.User.Claims
                .GroupBy(x => x.Type)
                .ToDictionary(x => x.Key, x => x.Select(y => y.Value).ToArray());
            claims["authentication_type"] = [context.User.Identity?.AuthenticationType ?? string.Empty];
            claims["mcp_feature"] = [context.Features.Get<McpCallerAuthenticationFeature>() == null ? "absent" : "present"];

            return claims;
        }

        private static Mock<IUserManagementService> UserStore()
        {
            var users = new Mock<IUserManagementService>();
            users
                .Setup(x => x.GetBearerUserCandidatesAsync(RestSubject, Issuer, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new BearerUserCandidate
                    {
                        User = new BeaconUserData
                        {
                            Id = 42,
                            ExternalId = RestSubject,
                            IdentityProvider = Issuer,
                            UserName = "rest",
                            DisplayName = "Rest Caller",
                            IsEnabled = true,
                            Roles = [new BeaconRoleData { Id = 1, Name = "Viewer", Level = 1 }]
                        }
                    }
                ]);

            return users;
        }

        private static Mock<IApiKeyService> ApiKeys()
        {
            var apiKeys = new Mock<IApiKeyService>();
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(ReadKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Credential(3, ReadKey, "[\"Read\"]", isSuperAdmin: false));
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(AdminOwnerKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Credential(4, AdminOwnerKey, "[\"Execute\"]", isSuperAdmin: true));
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(NoScopeKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Credential(5, NoScopeKey, "[]", isSuperAdmin: false));
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(InactiveOwnerKey, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ApiKeyOwnerInactiveException(90));

            return apiKeys;
        }

        private static ApiKeyCredential Credential(int id, string key, string scopes, bool isSuperAdmin)
        {
            return new ApiKeyCredential
            {
                Id = id,
                UserId = 7,
                Name = "pipeline",
                KeyHash = "hash",
                KeyPrefix = key[..12],
                Scopes = scopes,
                User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true, IsSuperAdmin = isSuperAdmin }
            };
        }

        private static Mock<IBeaconAuthorizationProvider> Authorization()
        {
            var authorization = new Mock<IBeaconAuthorizationProvider>();
            authorization.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
            authorization.Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);

            return authorization;
        }

        private static Mock<IMcpCallerMapper> CallerMapper()
        {
            var mapper = new Mock<IMcpCallerMapper>();
            mapper
                .Setup(x => x.MapAsync(It.Is<ClaimsPrincipal>(y => y.HasClaim("sub", McpSubject)), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new McpCaller(McpCallerKind.User, "Mcp Caller", "caller-hash", 42, [5], McpCallerScope.Execute, []));

            return mapper;
        }
    }

    // The session store: one active user.
    private sealed class SessionStore : IUserSessionStore
    {
        public Task<SessionUser?> FindAsync(string externalId, CancellationToken cancellationToken)
        {
            return Task.FromResult(externalId == "ana"
                ? new SessionUser { Id = 7, IsActive = true, RoleNames = ["Admin"] }
                : null);
        }

        public Task<bool> RenewStampAsync(int userId, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    // The host's claims transformation, as single sign-on hosts add one: the user "ana", by either of her identifiers, is
    // an Admin, and every principal it sees is marked.
    private sealed class AdminRoleTransformation : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            if (principal.Identity is not ClaimsIdentity { IsAuthenticated: true } identity || identity.HasClaim(x => x.Type == TransformedClaim))
            {
                return Task.FromResult(principal);
            }

            identity.AddClaim(new Claim(TransformedClaim, "yes"));
            var id = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (id is "ana" or "7")
            {
                identity.AddClaim(new Claim(ClaimTypes.Role, RoleService.RoleNames.Admin));
                identity.AddClaim(new Claim(BeaconClaims.Role, RoleService.RoleNames.Admin));
            }

            return Task.FromResult(principal);
        }
    }
}
