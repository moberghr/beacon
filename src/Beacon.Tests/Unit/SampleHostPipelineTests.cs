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
using Beacon.Gateway;
using Beacon.Tests.Common;
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
/// <para>
/// Not mirrored from <c>Program.cs</c>: <c>UseForwardedHeaders</c>, <c>UseHttpsRedirection</c>, <c>UseStaticFiles</c>,
/// <c>UseBeaconApiExceptionHandler</c> (the chain adds it first; pinned by <c>BeaconApiHostHelpersTests</c>), <c>UseAntiforgery</c>,
/// <c>MapOpenApi</c>, <c>MapBeaconApi</c>, <c>MapLoginEndpoints</c>, <c>MapSetupEndpoints</c>, <c>UseWarpUI</c> and
/// <c>MapBeaconUi</c>; the Warp dashboard, the SPA and the whoami and sign-in routes are <c>MapGet</c> stand-ins, so the
/// "spa" and "warp" assertions prove the Beacon middlewares' decisions, not the real dashboard or SPA. Bearer
/// authentication is disabled here (the sample enables it only when OIDC is configured with a JWKS endpoint), so the
/// JWT middleware's position for a bearer caller is pinned elsewhere: <c>BeaconAuthenticationScopeTests</c> (a JWT
/// through <c>UseBeaconAuthentication</c>) and <c>RestBearerUserBindingTests</c>.
/// <see cref="Program_KeepsTheMirroredCallsInTheOrderThisPipelineUses"/> fails when <c>Program.cs</c> leaves the chain
/// (<c>UseBeaconSecurityHeaders</c>, <c>UseStaticFiles</c>, <c>UseBeaconPipeline</c>, <c>MapBeaconOpenApi</c>,
/// <c>MapBeacon</c>, in that order) or adds back a call the chain makes.
/// </para>
/// <para>
/// Every test runs twice: on the individual calls the sample made before the chain and a host on the older entry points
/// still makes (<see cref="HostWiring.SampleCalls"/>), and on a host built with the fluent chain the sample uses now
/// (<see cref="HostWiring.Chain"/>: <c>AddBeacon(...).AddAI().AddMcp().AddApi().AddCookieAuth&lt;T&gt;()</c>,
/// <c>UseBeaconPipeline()</c> and <c>MapBeacon()</c>), which must behave the same.
/// </para>
/// </summary>
[TestFixture(HostWiring.SampleCalls)]
[TestFixture(HostWiring.Chain)]
public class SampleHostPipelineTests(SampleHostPipelineTests.HostWiring wiring)
{
    public enum HostWiring
    {
        SampleCalls,
        Chain
    }

    private readonly HostWiring _wiring = wiring;

    private const string ExecuteKey = "sk-sem_pipeline-execute-key";
    private const string ReadKey = "sk-sem_pipeline-read-key";
    // An initialize request: both MCP servers here (the bare SDK one and Beacon's own) answer it without a session, so a
    // 200 shows the caller got past authentication and the route policy.
    private const string McpBody =
        "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"pipeline-test\",\"version\":\"1.0\"}}}";

    [Test]
    public async Task CookieSession_FirstRequestAfterSignIn_IsAuthorized_AndReachesTheSpa_NotTheLoginPage()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);
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
        await using var host = await PipelineHost.StartAsync(_wiring);

        var page = await host.SendAsync(HttpMethod.Get, "/beacon/home");
        var api = await host.SendAsync(HttpMethod.Get, "/beacon/api/whoami");

        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().Be("/beacon/login");
        api.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "an API route answers 401 and is never redirected to an HTML page");
    }

    [Test]
    public async Task HostRoute_OutsideTheScope_IsNeverRedirected_WhateverTheFirstRunState([Values] bool firstRun)
    {
        await using var host = await PipelineHost.StartAsync(_wiring, firstRun: firstRun);

        var response = await host.SendAsync(HttpMethod.Get, "/reports");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task WarpDashboard_ListedInAdditionalPaths_IsReachedWithoutALoginRedirect_ByItsExactCasedPrefix()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);

        var response = await host.SendAsync(HttpMethod.Get, "/warp/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the dashboard enforces its own admin check");
        (await response.Content.ReadAsStringAsync()).Should().Be("warp");
    }

    [Test]
    public async Task WarpDashboard_MixedCasePath_IsRedirectedToLogin()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);

        var response = await host.SendAsync(HttpMethod.Get, "/WARP/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/beacon/login");
    }

    // Program.cs wires Beacon through the chain (the HostWiring.Chain fixture): UseBeaconPipeline in place of the five
    // pipeline calls the SampleCalls fixture makes, MapBeacon in place of its MCP and discovery mappings. None of those
    // calls may come back beside the chain: UseBeaconPipeline already adds them, and a second copy would run Beacon's
    // middlewares twice. The security headers come before UseStaticFiles (so the SPA's files carry them), and the OpenAPI
    // document is mapped with MapBeaconOpenApi (gated outside Development), never with a bare MapOpenApi beside it.
    [Test]
    public void Program_KeepsTheMirroredCallsInTheOrderThisPipelineUses()
    {
        var code = File.ReadAllLines(SampleProgramPath())
            .Select(x => x.Trim())
            .Where(x => !x.StartsWith("//", StringComparison.Ordinal))
            .ToList();
        string[] mirrored =
        [
            "app.UseBeaconSecurityHeaders();",
            "app.UseStaticFiles();",
            "app.UseBeaconPipeline();",
            "app.MapBeaconOpenApi();",
            "app.MapBeacon();"
        ];
        string[] replacedByTheChain =
        [
            "app.UseApiExceptionHandler(",
            "app.UseBeaconApiExceptionHandler(",
            "app.UseMcpResourceMetadataChallenge(",
            "app.UseBeaconAuthentication(",
            "app.UseAuthentication(",
            "app.UseAuthorization(",
            "app.UseMiddleware<LoginFormAuthMiddleware>(",
            "app.UseMiddleware<FirstRunSetupMiddleware>(",
            "app.UseAntiforgery(",
            "app.MapBeaconApi(",
            "app.MapLoginEndpoints(",
            "app.MapSetupEndpoints(",
            "app.MapBeaconMcp(",
            "app.MapMcpDiscovery(",
            "app.MapBeaconUi(",
            "app.MapOpenApi("
        ];

        var positions = mirrored
            .Select(x => code.FindIndex(y => y.StartsWith(x, StringComparison.Ordinal)))
            .ToList();

        positions.Should().OnlyContain(x => x >= 0, "every mirrored call is still in Program.cs");
        positions.Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        code.Should().NotContain(x => replacedByTheChain.Any(y => x.StartsWith(y, StringComparison.Ordinal)));
    }

    [Test]
    public async Task SampleAdditionalPaths_AreTheWarpDashboardAndTheOpenApiDocument()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);

        // The OpenAPI document is gated behind Beacon's sign-in outside Development.
        host.AdditionalPaths.Should().Equal("/warp", "/openapi");
    }

    [TestCase("/.well-known/oauth-protected-resource")]
    [TestCase("/.well-known/oauth-protected-resource/beacon/mcp")]
    [TestCase("/.well-known/mcp/server-card.json")]
    public async Task DiscoveryDocument_IsAnonymous200_WhileFirstRunSetupIsOpenAndTheLoginFormIsOn(string path)
    {
        await using var host = await PipelineHost.StartAsync(_wiring, firstRun: true);

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
        await using var host = await PipelineHost.StartAsync(_wiring, additionalPaths: ["/.well-known"]);

        var response = await host.SendAsync(HttpMethod.Get, path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the exact mapped paths are allow-listed, not the /.well-known prefix");
    }

    [TestCase("/.well-known/other")]
    [TestCase("/.well-known/mcp")]
    [TestCase("/.well-known/mcp/server-card.json/extra")]
    [TestCase("/.well-known/MCP/server-card.json")]
    public async Task OtherWellKnownPath_InsideTheScope_IsNotAnonymous_AndIsRedirectedToLogin(string path)
    {
        await using var host = await PipelineHost.StartAsync(_wiring, additionalPaths: ["/.well-known"]);

        var response = await host.SendAsync(HttpMethod.Get, path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be("/beacon/login");
    }

    [Test]
    public async Task FirstRun_RedirectsTheLoginPageToSetup_AndLetsTheSetupPageThrough()
    {
        await using var host = await PipelineHost.StartAsync(_wiring, firstRun: true);

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
        await using var host = await PipelineHost.StartAsync(_wiring, firstRun: false);

        var login = await host.SendAsync(HttpMethod.Get, "/beacon/login");

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        login.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task Mcp_WithoutCredentials_Is401WithTheResourceMetadataChallenge_NotALoginRedirect()
    {
        await using var host = await PipelineHost.StartAsync(_wiring, firstRun: true);

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
        response.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=\"https://beacon.example.test/.well-known/oauth-protected-resource\"");
    }

    [Test]
    public async Task Mcp_ApiKeyWithoutTheExecuteScope_IsForbidden()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody, apiKey: ReadKey);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task Mcp_ApiKeyWithTheExecuteScope_IsAdmitted_PastAuthenticationAndThePolicy()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody, apiKey: ExecuteKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task Mcp_ApiKeyCaller_NeedsNoCookie_AndTheKeyRunsBeforeASessionCookie()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);
        var cookie = await host.SignInAsync();

        var response = await host.SendAsync(HttpMethod.Post, "/beacon/mcp", body: McpBody, apiKey: ReadKey, cookie: cookie);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the Read-scoped key decides, the cookie beside it does not widen it");
    }

    [Test]
    public async Task SessionCookie_IsHttpOnly_Lax_AndNamedBeaconAuth_AndSecureOnlyOverHttps()
    {
        await using var host = await PipelineHost.StartAsync(_wiring);

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

    private static string SampleProgramPath()
    {
        return Path.Combine(Path.GetDirectoryName(SampleAppSettingsPath())!, "Program.cs");
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

        public static async Task<PipelineHost> StartAsync(HostWiring wiring, bool firstRun = false, string[]? additionalPaths = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();

            // The sample's own AdditionalPaths, plus any a test adds.
            var sample = new ConfigurationBuilder().AddJsonFile(SampleAppSettingsPath()).Build();
            var paths = sample.GetSection(AuthenticationPathOptions.SectionName).Get<AuthenticationPathOptions>()?.AdditionalPaths.ToList() ?? [];
            var sampleSettings = paths.Count;
            paths.AddRange(additionalPaths ?? []);
            var settings = BeaconHostChain.RequiredSettings();
            for (var i = 0; i < paths.Count; i++)
            {
                settings[$"{AuthenticationPathOptions.SectionName}:AdditionalPaths:{i}"] = paths[i];
            }

            builder.Configuration.AddInMemoryCollection(settings);
            sampleSettings.Should().BeGreaterThan(0, "the sample lists the Warp dashboard");

            if (wiring == HostWiring.Chain)
            {
                return await StartChainAsync(builder, firstRun, paths);
            }

            var beaconConfiguration = new BeaconConfiguration();
            beaconConfiguration.Authentication.EnableLoginForm = true;
            beaconConfiguration.UserManagement.Enabled = true;

            builder.Services.AddOptions<AuthenticationPathOptions>()
                .Bind(builder.Configuration.GetSection(AuthenticationPathOptions.SectionName));
            builder.Services.AddSingleton<AuthenticationPathPrefixes>();
            builder.Services.AddSingleton(beaconConfiguration);
            AddDoubles(builder.Services, firstRun);
            builder.Services.AddSingleton(new HttpClient());
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
            MapStandIns(app);

            await app.StartAsync();

            return new PipelineHost(app, paths.ToArray());
        }

        // The same host through the fluent chain: Beacon's own registrations, then the same doubles (registered last, so
        // they are the ones resolved), UseBeaconPipeline in place of the five pipeline calls and MapBeacon in place of the
        // MCP and discovery mappings (it maps the API, sign-in, setup and shell endpoints too; the stand-ins outrank the
        // shell's fallback).
        private static async Task<PipelineHost> StartChainAsync(WebApplicationBuilder builder, bool firstRun, List<string> paths)
        {
            BeaconHostChain.Add(builder.Services, builder.Configuration, x =>
            {
                x.Authentication.EnableLoginForm = true;
                x.EnableUserManagement();
            });
            AddDoubles(builder.Services, firstRun);

            var app = builder.Build();

            app.UseBeaconPipeline();

            app.MapGet("/beacon/api/whoami", (HttpContext context) => context.User.Identity?.AuthenticationType ?? string.Empty)
                .RequireAuthorization();
            app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                    CookieAuthenticationDefaults.AuthenticationScheme));
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            }).AllowAnonymous();
            app.MapBeacon();
            MapStandIns(app);

            await app.StartAsync();

            return new PipelineHost(app, paths.ToArray());
        }

        private static void AddDoubles(IServiceCollection services, bool firstRun)
        {
            services.AddSingleton(UserStore(firstRun).Object);
            services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            services.AddSingleton<IUserSessionStore>(new BeaconAuthenticationScopeTests.CountingSessionStore());
            services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 0, AbsoluteLifetimeHours = 12 }));
            services.AddSingleton(ApiKeys().Object);
            services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()) == Task.FromResult(true)));
            services.AddSingleton(Mock.Of<IMcpCallerMapper>());
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
        }

        private static void MapStandIns(WebApplication app)
        {
            app.MapGet("/warp/{**page}", () => "warp");
            app.MapGet("/reports", () => "reports");
            app.MapGet("/beacon/{**page}", () => "spa");
            app.MapGet("/.well-known/{**page}", () => "stand-in");
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
