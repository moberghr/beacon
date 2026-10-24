using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Beacon.Api.Authentication;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Services.Security;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Beacon's authentication schemes: the API-key and bearer handlers, the <c>Beacon</c> policy scheme that picks between
/// them and the cookie, the one problem body every refusal carries (a bearer refusal included), and a host whose only
/// scheme of its own is still its default after Beacon's schemes are added. No database (§4.7).
/// </summary>
[TestFixture]
public class BeaconAuthenticationSchemeTests
{
    private const string ApiKey = "sk-sem_scheme-test-key";

    [TestCase("Bearer sk-sem_abc", true, BeaconAuthenticationDefaults.ApiKeyScheme)]
    [TestCase("bearer SK-SEM_abc", false, BeaconAuthenticationDefaults.ApiKeyScheme)]
    [TestCase("Bearer eyJ.token.value", true, BeaconAuthenticationDefaults.BearerScheme)]
    [TestCase("Bearer eyJ.token.value", false, CookieAuthenticationDefaults.AuthenticationScheme)]
    [TestCase("Bearer    ", true, CookieAuthenticationDefaults.AuthenticationScheme)]
    [TestCase("Basic dXNlcjpwYXNz", true, CookieAuthenticationDefaults.AuthenticationScheme)]
    [TestCase(null, true, CookieAuthenticationDefaults.AuthenticationScheme)]
    public void SelectScheme_FollowsTheCredentialPresented(string? authorization, bool bearerEnabled, string expected)
    {
        var services = new ServiceCollection()
            .AddSingleton(new JwtAuthenticationOptions { EnableBearerAuthentication = bearerEnabled })
            .BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        if (authorization != null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        BeaconAuthenticationDefaults.SelectScheme(context).Should().Be(expected);
    }

    [Test]
    public async Task ApiKeyHandler_OnAHostRoute_NeitherAcceptsNorRefusesTheKey()
    {
        await using var host = await SchemeHost.StartAsync();

        var hostRoute = await host.AuthenticateAsync("/reports", BeaconAuthenticationDefaults.ApiKeyScheme, $"Bearer {ApiKey}");
        var beaconRoute = await host.AuthenticateAsync("/beacon/api/x", BeaconAuthenticationDefaults.ApiKeyScheme, $"Bearer {ApiKey}");
        var refused = await host.AuthenticateAsync("/beacon/api/x", BeaconAuthenticationDefaults.ApiKeyScheme, "Bearer sk-sem_unknown");

        hostRoute.None.Should().BeTrue();
        beaconRoute.Succeeded.Should().BeTrue();
        beaconRoute.Principal!.Identity!.AuthenticationType.Should().Be(McpCallerClaimTypes.ApiKeyAuthenticationType);
        refused.Failure.Should().NotBeNull();
    }

    [Test]
    public async Task BearerHandler_WithoutBearerAuthenticationConfigured_AuthenticatesNobody()
    {
        await using var host = await SchemeHost.StartAsync();

        var result = await host.AuthenticateAsync("/beacon/api/x", BeaconAuthenticationDefaults.BearerScheme, "Bearer eyJ.token.value");

        result.None.Should().BeTrue();
    }

    [Test]
    public async Task PolicyScheme_NamedByARoute_AuthenticatesTheKeyAndTheCookie()
    {
        await using var host = await SchemeHost.StartAsync();
        var cookie = await host.SignInAsync();

        var asKey = await host.SendAsync("/beacon/api/by-scheme", authorization: $"Bearer {ApiKey}");
        var asSession = await host.SendAsync("/beacon/api/by-scheme", cookie: cookie);
        var refusedKey = await host.SendAsync("/beacon/api/by-scheme", authorization: "Bearer sk-sem_unknown");

        (await asKey.Content.ReadAsStringAsync()).Should().Be(McpCallerClaimTypes.ApiKeyAuthenticationType);
        (await asSession.Content.ReadAsStringAsync()).Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        refusedKey.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task RefusedBearerToken_IsAProblemBody_WithItsOAuthErrorAndChallenge()
    {
        await using var host = await SchemeHost.StartAsync(bearer: true);

        var response = await host.SendAsync("/beacon/api/whoami", authorization: "Bearer not-a-token");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer error=\"invalid_token\"");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().Should().Be(401);
        problem.GetProperty("title").GetString().Should().Be("Unauthorized");
        problem.GetProperty("detail").GetString().Should().Be("The bearer token was not accepted.");
        problem.GetProperty("error").GetString().Should().Be("invalid_token");
    }

    [Test]
    public async Task HostWithOneSchemeOfItsOwn_KeepsItAsTheDefault_AfterBeaconsSchemesAreAdded()
    {
        await using var cookieOnly = await SchemeHost.StartAsync();
        await using var withHostScheme = await SchemeHost.StartAsync(hostScheme: true);

        (await cookieOnly.DefaultAuthenticateSchemeAsync()).Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        (await withHostScheme.DefaultAuthenticateSchemeAsync()).Should().BeNull("two schemes of the host's own leave no default, as before");
    }

    private sealed class SchemeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private SchemeHost(WebApplication app)
        {
            _app = app;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<SchemeHost> StartAsync(bool bearer = false, bool hostScheme = false)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<IUserSessionStore>(new BeaconAuthenticationScopeTests.CountingSessionStore());
            builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 0, AbsoluteLifetimeHours = 12 }));
            builder.Services.AddSingleton(ApiKeys().Object);
            builder.Services.AddSingleton(Mock.Of<IMcpCallerMapper>());
            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBeaconCookieAuthentication("/beacon");
            if (bearer)
            {
                builder.Services.AddBeaconJwtAuthentication(x =>
                {
                    x.EnableBearerAuthentication = true;
                    x.Validation.SigningKey = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(48));
                    x.Validation.ValidIssuer = "https://idp.example.test";
                    x.Validation.ValidAudience = "api://beacon";
                });
            }

            if (hostScheme)
            {
                builder.Services.AddAuthentication().AddCookie("Host");
            }

            builder.Services.AddAuthorization();

            var app = builder.Build();
            app.UseRouting();
            app.UseBeaconAuthentication();
            app.UseAuthorization();
            app.MapGet("/beacon/api/whoami", WhoAmI).RequireAuthorization();
            app.MapGet("/beacon/api/by-scheme", WhoAmI)
                .RequireAuthorization(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(BeaconAuthenticationDefaults.AuthenticationScheme)
                    .RequireAuthenticatedUser()
                    .Build());
            app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                    CookieAuthenticationDefaults.AuthenticationScheme));
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            });
            await app.StartAsync();

            return new SchemeHost(app);
        }

        public async Task<string> SignInAsync()
        {
            var response = await Client.GetAsync("/beacon/api/test/sign-in");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return response.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal))
                .Split(';')[0];
        }

        public async Task<HttpResponseMessage> SendAsync(string path, string? authorization = null, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (authorization != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return await Client.SendAsync(request);
        }

        // The scheme's own answer for a request on the path, without the pipeline.
        public async Task<AuthenticateResult> AuthenticateAsync(string path, string scheme, string authorization)
        {
            using var scope = _app.Services.CreateScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Path = path;
            context.Request.Headers.Authorization = authorization;
            var handler = await scope.ServiceProvider.GetRequiredService<IAuthenticationHandlerProvider>().GetHandlerAsync(context, scheme);

            return await handler!.AuthenticateAsync();
        }

        public async Task<string?> DefaultAuthenticateSchemeAsync()
        {
            return (await _app.Services.GetRequiredService<IAuthenticationSchemeProvider>().GetDefaultAuthenticateSchemeAsync())?.Name;
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }

        private static string WhoAmI(HttpContext context)
        {
            return context.User.Identity?.AuthenticationType ?? string.Empty;
        }

        private static Mock<IApiKeyService> ApiKeys()
        {
            var apiKeys = new Mock<IApiKeyService>();
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(ApiKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiKeyCredential
                {
                    Id = 3,
                    UserId = 7,
                    Name = "scheme",
                    KeyHash = "hash",
                    KeyPrefix = ApiKey[..12],
                    Scopes = "[\"Read\"]",
                    User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true }
                });

            return apiKeys;
        }
    }
}
