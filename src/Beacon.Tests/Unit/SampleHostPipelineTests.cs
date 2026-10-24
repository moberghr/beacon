using System.Net;
using System.Security.Claims;
using System.Text;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.MCP;
using Beacon.MCP.Discovery;
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
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The sample host's request pipeline, in the sample's order and built from the same public extension methods:
/// <c>UseMcpResourceMetadataChallenge</c>, <c>UseBeaconAuthentication</c>, <c>UseAuthorization</c>, the login-form and
/// first-run middlewares, <c>MapBeaconMcp</c> and <c>MapMcpDiscovery</c>, with <c>Beacon:Authentication:AdditionalPaths</c>
/// read from the sample's own <c>appsettings.json</c>. The sample's <c>Program</c> cannot start without a database (it
/// migrates the Warp schema before it serves), so its wiring is rebuilt here without one (§4.7); the database-free pieces
/// the sample adds (the user store, the API-key store, the SPA and the Warp dashboard) are stand-ins. These tests pin the
/// observable behaviour of that pipeline, so a change of how a host wires it must leave them green.
/// </summary>
[TestFixture]
public class SampleHostPipelineTests
{
    private const string ExecuteKey = "sk-sem_pipeline-execute-key";
    private const string ReadKey = "sk-sem_pipeline-read-key";
    private const string McpBody = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}";

    [Test]
    public async Task CookieSession_FirstRequestAfterSignIn_IsAuthorized_AndReachesTheSpa_NotTheLoginPage()
    {
        await using var host = await PipelineHost.StartAsync();
        var cookie = await host.SignInAsync();

        var api = await host.SendAsync(HttpMethod.Get, "/beacon/api/whoami", cookie: cookie);
        var page = await host.SendAsync(HttpMethod.Get, "/beacon/home", cookie: cookie);

        api.StatusCode.Should().Be(HttpStatusCode.OK);
        (await api.Content.ReadAsStringAsync()).Should().Be(CookieAuthenticationDefaults.AuthenticationScheme);
        page.StatusCode.Should().Be(HttpStatusCode.OK, "the cookie populated the user before the login-form check");
        (await page.Content.ReadAsStringAsync()).Should().Be("spa");
    }

    [Test]
    public async Task AnonymousUiPage_InsideTheBeaconScope_RedirectsToTheLoginPage_AndAnApiPathIsNot()
    {
        await using var host = await PipelineHost.StartAsync();

        var page = await host.SendAsync(HttpMethod.Get, "/beacon/home");
        var api = await host.SendAsync(HttpMethod.Get, "/beacon/api/whoami");

        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().Be("/beacon/login");
        api.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an API route answers 401 and is never redirected to an HTML page");
    }

    [Test]
    public async Task HostRoute_OutsideTheScope_IsNeverRedirected_WhateverTheFirstRunState([Values] bool firstRun)
    {
        await using var host = await PipelineHost.StartAsync(firstRun: firstRun);

        var response = await host.SendAsync(HttpMethod.Get, "/reports");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task WarpDashboard_ListedInAdditionalPaths_IsReachedWithoutALoginRedirect_ByItsExactCasedPrefix()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync(HttpMethod.Get, "/warp/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the dashboard enforces its own admin check");
        (await response.Content.ReadAsStringAsync()).Should().Be("warp");
    }

    [Test]
    public async Task WarpDashboard_MixedCasePath_IsRedirectedToLogin_NotServedByTheSpaFallback()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync(HttpMethod.Get, "/WARP/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/beacon/login");
    }

    [Test]
    public async Task SampleAdditionalPaths_AreTheWarpDashboardOnly()
    {
        await using var host = await PipelineHost.StartAsync();

        host.AdditionalPaths.Should().Equal("/warp");
    }

    [TestCase("/.well-known/oauth-protected-resource")]
    [TestCase("/.well-known/oauth-protected-resource/beacon/mcp")]
    [TestCase("/.well-known/mcp/server-card.json")]
    public async Task DiscoveryDocument_IsAnonymous200_WhileFirstRunSetupIsOpenAndTheLoginFormIsOn(string path)
    {
        await using var host = await PipelineHost.StartAsync(firstRun: true);

        var response = await host.SendAsync(HttpMethod.Get, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [TestCase("/.well-known/oauth-protected-resource")]
    [TestCase("/.well-known/oauth-protected-resource/beacon/mcp")]
    [TestCase("/.well-known/mcp/server-card.json")]
    public async Task DiscoveryDocument_StaysAnonymous_EvenWhenTheHostListsWellKnownInAdditionalPaths(string path)
    {
        await using var host = await PipelineHost.StartAsync(additionalPaths: ["/.well-known"]);

        var response = await host.SendAsync(HttpMethod.Get, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the exact mapped paths are allow-listed, not the /.well-known prefix");
    }

    [TestCase("/.well-known/other")]
    [TestCase("/.well-known/mcp")]
    [TestCase("/.well-known/mcp/server-card.json/extra")]
    [TestCase("/.well-known/MCP/server-card.json")]
    public async Task OtherWellKnownPath_InsideTheScope_IsNotAnonymous_AndIsRedirectedToLogin(string path)
    {
        await using var host = await PipelineHost.StartAsync(additionalPaths: ["/.well-known"]);

        var response = await host.SendAsync(HttpMethod.Get, path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/beacon/login");
    }

    [Test]
    public async Task FirstRun_RedirectsTheLoginPageToSetup_AndLetsTheSetupPageThrough()
    {
        await using var host = await PipelineHost.StartAsync(firstRun: true);

        var login = await host.SendAsync(HttpMethod.Get, "/beacon/login");
        var setup = await host.SendAsync(HttpMethod.Get, "/beacon/setup");

        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().Be("/beacon/setup");
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        (await setup.Content.ReadAsStringAsync()).Should().Be("spa");
    }

    [Test]
    public async Task AfterFirstRun_TheLoginPageIsServed_NotRedirectedToSetup()
    {
        await using var host = await PipelineHost.StartAsync(firstRun: false);

        var login = await host.SendAsync(HttpMethod.Get, "/beacon/login");

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        login.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task Mcp_WithoutCredentials_Is401WithTheResourceMetadataChallenge_NotALoginRedirect()
    {
        await using var host = await PipelineHost.StartAsync(firstRun: true);

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
        response.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=\"https://beacon.example.test/.well-known/oauth-protected-resource\"");
    }

    [Test]
    public async Task Mcp_ApiKeyWithoutTheExecuteScope_IsForbidden()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody, apiKey: ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Mcp_ApiKeyWithTheExecuteScope_IsAdmitted_PastAuthenticationAndThePolicy()
    {
        await using var host = await PipelineHost.StartAsync();

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody, apiKey: ExecuteKey);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().NotBe(HttpStatusCode.Redirect);
    }

    [Test]
    public async Task Mcp_ApiKeyCaller_NeedsNoCookie_AndTheKeyRunsBeforeASessionCookie()
    {
        await using var host = await PipelineHost.StartAsync();
        var cookie = await host.SignInAsync();

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody, apiKey: ReadKey, cookie: cookie);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the Read-scoped key decides, the cookie beside it does not widen it");
    }

    [Test]
    public async Task SessionCookie_IsHttpOnly_Lax_AndNamedBeaconAuth_AndSecureOnlyOverHttps()
    {
        await using var host = await PipelineHost.StartAsync();

        var overHttp = await host.Client.GetAsync("/beacon/api/test/sign-in");
        using var httpsClient = host.CreateHttpsClient();
        var overHttps = await httpsClient.GetAsync("/beacon/api/test/sign-in");

        var plain = SessionCookieHeader(overHttp).ToLowerInvariant();
        var secure = SessionCookieHeader(overHttps).ToLowerInvariant();
        plain.Should().Contain("httponly").And.Contain("samesite=lax").And.NotContain("secure");
        secure.Should().Contain("httponly").And.Contain("samesite=lax").And.Contain("secure");
        plain.Should().Contain("path=/");
    }

    private static string SessionCookieHeader(HttpResponseMessage response)
    {
        return response.Headers.GetValues("Set-Cookie")
            .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal));
    }

    private static string SampleAppSettingsPath()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");

        return Path.Combine(directory!.FullName, "src", "Beacon.SampleProject", "appsettings.json");
    }

    private sealed class PipelineHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private PipelineHost(WebApplication app, string[] additionalPaths)
        {
            _app = app;
            AdditionalPaths = additionalPaths;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public string[] AdditionalPaths { get; }

        public static async Task<PipelineHost> StartAsync(bool firstRun = false, string[]? additionalPaths = null)
        {
            var beaconConfiguration = new BeaconConfiguration();
            beaconConfiguration.Authentication.EnableLoginForm = true;
            beaconConfiguration.UserManagement.Enabled = true;

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            // The sample's own AdditionalPaths, plus any a test adds.
            var sample = new ConfigurationBuilder().AddJsonFile(SampleAppSettingsPath()).Build();
            var paths = sample.GetSection(AuthenticationPathOptions.SectionName).Get<AuthenticationPathOptions>()?.AdditionalPaths.ToList() ?? [];
            var sampleSettings = paths.Count;
            paths.AddRange(additionalPaths ?? []);
            var settings = new Dictionary<string, string?> { ["Beacon:PublicBaseUrl"] = "https://beacon.example.test" };
            for (var i = 0; i < paths.Count; i++)
            {
                settings[$"{AuthenticationPathOptions.SectionName}:AdditionalPaths:{i}"] = paths[i];
            }

            builder.Configuration.AddInMemoryCollection(settings);
            sampleSettings.Should().BeGreaterThan(0, "the sample lists the Warp dashboard");

            builder.Services.AddOptions<AuthenticationPathOptions>()
                .Bind(builder.Configuration.GetSection(AuthenticationPathOptions.SectionName));
            builder.Services.AddSingleton<AuthenticationPathPrefixes>();
            builder.Services.AddSingleton(beaconConfiguration);
            builder.Services.AddSingleton(UserStore(firstRun).Object);
            builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            builder.Services.AddSingleton<IUserSessionStore>(new BeaconAuthenticationScopeTests.CountingSessionStore());
            builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 0, AbsoluteLifetimeHours = 12 }));
            builder.Services.AddSingleton(ApiKeys().Object);
            builder.Services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()) == Task.FromResult(true)));
            builder.Services.AddSingleton(Mock.Of<IMcpCallerMapper>());
            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBeaconCookieAuthentication("/beacon");
            builder.Services.AddBeaconJwtAuthentication(x => x.EnableBearerAuthentication = false);
            builder.Services.AddMcpServer().WithHttpTransport();
            builder.Services.AddBeaconApiAuthorization();

            var app = builder.Build();

            // The sample's order (Program.cs "MIDDLEWARE PIPELINE").
            app.UseMcpResourceMetadataChallenge();
            app.UseBeaconAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, "/beacon");
            app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, "/beacon");

            app.MapGet("/beacon/api/whoami", (HttpContext context) => context.User.Identity?.AuthenticationType ?? string.Empty)
                .RequireAuthorization();
            app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                    CookieAuthenticationDefaults.AuthenticationScheme));
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            }).AllowAnonymous();
            app.MapBeaconMcp();
            app.MapMcpDiscovery();
            app.MapGet("/warp/{**page}", () => "warp");
            app.MapGet("/reports", () => "reports");
            app.MapGet("/beacon/{**page}", () => "spa");
            app.MapGet("/.well-known/{**page}", () => "stand-in");

            await app.StartAsync();

            return new PipelineHost(app, paths.ToArray());
        }

        public HttpClient CreateHttpsClient()
        {
            var client = _app.GetTestClient();
            client.BaseAddress = new Uri("https://localhost");

            return client;
        }

        public async Task<string> SignInAsync()
        {
            var response = await Client.GetAsync("/beacon/api/test/sign-in");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return SessionCookieHeader(response).Split(';')[0];
        }

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body = null, string? apiKey = null, string? cookie = null)
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
                request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            }

            if (apiKey != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }

            if (cookie != null)
            {
                request.Headers.Add("Cookie", cookie);
            }

            return await Client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }

        private static Mock<IUserManagementService> UserStore(bool firstRun)
        {
            var users = new Mock<IUserManagementService>();
            users
                .Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstRun);

            return users;
        }

        private static Mock<IApiKeyService> ApiKeys()
        {
            var apiKeys = new Mock<IApiKeyService>();
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(ExecuteKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Credential(1, ExecuteKey, "[\"Execute\"]"));
            apiKeys
                .Setup(x => x.ValidateApiKeyAsync(ReadKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Credential(2, ReadKey, "[\"Read\"]"));

            return apiKeys;
        }

        private static ApiKeyCredential Credential(int id, string key, string scopes)
        {
            return new ApiKeyCredential
            {
                Id = id,
                UserId = 7,
                Name = "pipeline",
                KeyHash = "hash",
                KeyPrefix = key[..12],
                Scopes = scopes,
                User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true, IsSuperAdmin = true }
            };
        }
    }
}
