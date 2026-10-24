using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
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
using Beacon.MCP.Discovery;
using Beacon.UI;
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
/// What a signed-out browser gets from Beacon's routes with the login form on, in the sample's order
/// (<c>UseStaticFiles</c>, <c>UseBeaconAuthentication</c>, <c>UseAuthorization</c>, the login-form and first-run
/// middlewares) with the real <c>MapBeaconUi</c> and <c>MapMcpDiscovery</c> over a web root holding the SPA's
/// <c>index.html</c> and one asset: the redirect target of every UI page, the pages a signed-out browser is let through
/// to, static assets, the Warp dashboard by its exact-cased prefix, an API route's 401 instead of a redirect, and the
/// host's own routes left alone. The API routes, the Warp dashboard, the OpenAPI document and the host routes are
/// <c>MapGet</c> stand-ins with the metadata their real counterparts carry. No database (§4.7).
/// </summary>
[TestFixture]
public class BeaconUiSignInRedirectTests
{
    private const string LoginPage = "/beacon/login";
    private const string IndexMarker = "<title>beacon-ui-test</title>";
    private const string AssetBody = "console.log('beacon');";
    private const string ExecuteKey = "sk-sem_redirect-execute-key";

    [TestCase("/beacon")]
    [TestCase("/beacon/")]
    [TestCase("/beacon/home")]
    [TestCase("/beacon/projects/12")]
    [TestCase("/beacon/queries/7/edit")]
    [TestCase("/beacon/a.b/c")]
    [TestCase("/BEACON/home")]
    public async Task Anonymous_OnAUiPage_IsRedirectedToTheLoginPage(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage, "the target carries neither a return URL nor the host");
    }

    [Test]
    public async Task Anonymous_OnAUiPage_IsRedirected_AlsoWhenItAcceptsJson()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/beacon/home", accept: "application/json");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage);
    }

    [Test]
    public async Task Anonymous_OnAUiPage_WithABearerValueThatIsNoCredential_IsRedirectedToTheLoginPage()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/beacon/home", bearer: "not-a-key");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage);
    }

    [TestCase("/beacon/LOGIN")]
    [TestCase("/BEACON/login")]
    [TestCase("/beacon/login/extra")]
    [TestCase("/beacon/loginx")]
    [TestCase("/beacon/Logout")]
    [TestCase("/beacon/ERROR")]
    public async Task Anonymous_OnALookAlikeOfAnAuthPage_IsRedirectedToTheLoginPage(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage);
    }

    [TestCase("/beacon/home")]
    [TestCase("/beacon")]
    public async Task SignedIn_OnAUiPage_GetsTheSpa(string path)
    {
        await using var host = await UiHost.StartAsync();
        var cookie = await host.SignInAsync();

        var response = await host.SendAsync(path, cookie: cookie);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
    }

    [Test]
    public async Task AcceptedApiKey_OnAUiPage_GetsTheSpa()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/beacon/home", bearer: ExecuteKey);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
    }

    [TestCase("/beacon/login")]
    [TestCase("/beacon/logout")]
    [TestCase("/beacon/error")]
    [TestCase("/beacon/setup")]
    [TestCase("/beacon/SETUP")]
    public async Task Anonymous_OnAnAuthPage_GetsTheSpa(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
        (await response.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
    }

    [Test]
    public async Task Anonymous_OnTheSetupPage_WithoutUserManagement_IsRedirectedToTheLoginPage()
    {
        await using var host = await UiHost.StartAsync(userManagement: false);

        var response = await host.SendAsync("/beacon/setup");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage);
    }

    [Test]
    public async Task CustomLoginPath_IsTheRedirectTarget_AndIsItselfLetThrough()
    {
        await using var host = await UiHost.StartAsync(loginPath: "/sign-in");

        var page = await host.SendAsync("/beacon/home");
        var login = await host.SendAsync("/beacon/sign-in");

        page.StatusCode.Should().Be(HttpStatusCode.Redirect);
        page.Headers.Location!.OriginalString.Should().Be("/beacon/sign-in");
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await login.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
    }

    [Test]
    public async Task FirstRun_SendsAUiPageToTheLoginPage_AndTheLoginPageToSetup()
    {
        await using var host = await UiHost.StartAsync(firstRun: true);

        var page = await host.SendAsync("/beacon/home");
        var login = await host.SendAsync(LoginPage);
        var setup = await host.SendAsync("/beacon/setup");

        page.Headers.Location!.OriginalString.Should().Be(LoginPage);
        login.StatusCode.Should().Be(HttpStatusCode.Redirect);
        login.Headers.Location!.OriginalString.Should().Be("/beacon/setup");
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
        (await setup.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
    }

    [Test]
    public async Task LoginFormOff_ServesTheSpaToAnAnonymousCaller()
    {
        await using var host = await UiHost.StartAsync(loginForm: false);

        var response = await host.SendAsync("/beacon/home");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Contain(IndexMarker);
    }

    [Test]
    public async Task StaticAsset_IsServedToAnAnonymousCaller()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/beacon/assets/app.js");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(AssetBody);
    }

    [TestCase("/beacon/assets/missing.js")]
    [TestCase("/beacon/assets/missing.css")]
    [TestCase("/beacon/favicon.ico")]
    [TestCase("/beacon/_content/lib/missing.txt")]
    public async Task MissingStaticAsset_Is404_NotALoginRedirect(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task UnknownFileUnderTheUi_IsRedirectedToTheLoginPage()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/beacon/notes.txt");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage);
    }

    [Test]
    public async Task WarpDashboard_ByItsExactCasedPrefix_IsLeftToTheDashboard()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/warp/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be("warp");
    }

    [TestCase("/WARP/jobs")]
    [TestCase("/Warp")]
    public async Task WarpDashboard_MixedCase_IsRedirectedToTheLoginPage(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Be(LoginPage);
    }

    [TestCase(null)]
    [TestCase("application/json")]
    public async Task Anonymous_OnAnAuthorizedApiRoute_Is401ProblemDetails_NotARedirect(string? accept)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/beacon/api/whoami", accept: accept);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.Location.Should().BeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetInt32().Should().Be(401);
    }

    [TestCase("/beacon/api/anonymous")]
    [TestCase("/beacon/api/unmarked")]
    [TestCase("/beacon/api/setup/status")]
    public async Task Anonymous_OnAnApiRouteThatNeedsNoSignIn_IsServed(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
    }

    [TestCase("/beacon/api/nothing-here")]
    [TestCase("/beacon/mcp/nothing-here")]
    public async Task Anonymous_OnAnUnmappedApiPath_IsNotRedirected(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Location.Should().BeNull();
    }

    [TestCase("/.well-known/oauth-protected-resource")]
    [TestCase("/.well-known/oauth-protected-resource/beacon/mcp")]
    [TestCase("/.well-known/mcp/server-card.json")]
    public async Task DiscoveryDocument_IsAnonymous200(string path)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Location.Should().BeNull();
    }

    [TestCase("/reports", "reports")]
    [TestCase("/login", "host-login")]
    [TestCase("/openapi/v1.json", "openapi")]
    public async Task HostRoute_IsLeftToTheHost(string path, string body)
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync()).Should().Be(body);
    }

    [Test]
    public async Task UnmappedHostRoute_Is404_NotALoginRedirect()
    {
        await using var host = await UiHost.StartAsync();

        var response = await host.SendAsync("/.well-known/other");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.Location.Should().BeNull();
    }

    [Test]
    public async Task HostWithoutTheLoginFormMiddleware_StillRedirectsAUiPage_AndServesTheAuthPages()
    {
        await using var host = await UiHost.StartAsync(loginFormMiddleware: false);

        var page = await host.SendAsync("/beacon/home");
        var login = await host.SendAsync(LoginPage);
        var setup = await host.SendAsync("/beacon/setup");

        page.StatusCode.Should().Be(HttpStatusCode.Redirect, "MapBeaconUi's pages carry their own authorization");
        page.Headers.Location!.OriginalString.Should().Be(LoginPage);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        setup.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void UiPagePolicyNames_AreTheOnesBeaconApiRegisters()
    {
        BeaconUiEndpointRouteBuilderExtensions.PagePolicyName.Should().Be(BeaconUiPagePolicies.PagePolicyName);
        BeaconUiEndpointRouteBuilderExtensions.SetupPagePolicyName.Should().Be(BeaconUiPagePolicies.SetupPagePolicyName);
        BeaconUiEndpointRouteBuilderExtensions.BasePath.Should().Be(AuthenticationPathOptions.BeaconPath, "the page policies and the redirect use Core's constant");
    }

    private sealed class UiHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly string _webRoot;

        private UiHost(WebApplication app, string webRoot)
        {
            _app = app;
            _webRoot = webRoot;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public static async Task<UiHost> StartAsync(
            bool loginForm = true,
            bool userManagement = true,
            bool firstRun = false,
            string? loginPath = null,
            bool loginFormMiddleware = true)
        {
            var configuration = new BeaconConfiguration();
            configuration.Authentication.EnableLoginForm = loginForm;
            configuration.UserManagement.Enabled = userManagement;
            if (loginPath != null)
            {
                configuration.Authentication.LoginPath = loginPath;
            }

            var webRoot = CreateWebRoot();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing", WebRootPath = webRoot });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:PublicBaseUrl"] = "https://beacon.example.test",
                [$"{AuthenticationPathOptions.SectionName}:AdditionalPaths:0"] = "/warp"
            });

            builder.Services.AddOptions<AuthenticationPathOptions>()
                .Bind(builder.Configuration.GetSection(AuthenticationPathOptions.SectionName));
            builder.Services.AddSingleton<AuthenticationPathPrefixes>();
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(UserStore(firstRun).Object);
            builder.Services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
            builder.Services.AddSingleton<IUserSessionStore>(new BeaconAuthenticationScopeTests.CountingSessionStore());
            builder.Services.AddSingleton(Options.Create(new SessionLifetimeOptions { RevalidateMinutes = 0, AbsoluteLifetimeHours = 12 }));
            builder.Services.AddSingleton(ApiKeys().Object);
            builder.Services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>()) == Task.FromResult(true)));
            builder.Services.AddSingleton(Mock.Of<IMcpCallerMapper>());
            builder.Services.AddSingleton(new HttpClient());
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddBeaconCookieAuthentication(BeaconUiEndpointRouteBuilderExtensions.BasePath);
            builder.Services.AddBeaconJwtAuthentication(x => x.EnableBearerAuthentication = false);
            builder.Services.AddMcpServer().WithHttpTransport();
            builder.Services.AddBeaconApiAuthorization();

            var app = builder.Build();

            // The sample's order (Program.cs "MIDDLEWARE PIPELINE").
            app.UseStaticFiles();
            app.UseBeaconAuthentication();
            app.UseAuthorization();
            if (loginForm && loginFormMiddleware)
            {
                app.UseMiddleware<LoginFormAuthMiddleware>(configuration, BeaconUiEndpointRouteBuilderExtensions.BasePath);
            }

            if (userManagement)
            {
                app.UseMiddleware<FirstRunSetupMiddleware>(configuration, BeaconUiEndpointRouteBuilderExtensions.BasePath);
            }

            app.MapGet("/beacon/api/whoami", (HttpContext context) => context.User.Identity?.AuthenticationType ?? string.Empty)
                .RequireAuthorization(BeaconApiEndpoints.AuthPolicyName);
            app.MapGet("/beacon/api/anonymous", () => "anonymous")
                .AllowAnonymous();
            app.MapGet("/beacon/api/unmarked", () => "unmarked");
            app.MapGet("/beacon/api/setup/status", () => "setup-status")
                .AllowAnonymous();
            app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                    CookieAuthenticationDefaults.AuthenticationScheme));
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            }).AllowAnonymous();
            app.MapMcpDiscovery();
            app.MapGet("/warp/{**page}", () => "warp");
            app.MapGet("/reports", () => "reports");
            app.MapGet("/login", () => "host-login");
            app.MapGet("/openapi/v1.json", () => "openapi");
            app.MapBeaconUi();

            await app.StartAsync();

            return new UiHost(app, webRoot);
        }

        public async Task<string> SignInAsync()
        {
            var response = await Client.GetAsync("/beacon/api/test/sign-in");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return response.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal))
                .Split(';')[0];
        }

        public async Task<HttpResponseMessage> SendAsync(string path, string? accept = null, string? bearer = null, string? cookie = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            if (accept != null)
            {
                request.Headers.TryAddWithoutValidation("Accept", accept);
            }

            if (bearer != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {bearer}");
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
            try
            {
                Directory.Delete(_webRoot, recursive: true);
            }
            catch (IOException)
            {
                // A file the static-files middleware still holds: the temporary folder is left to the OS.
            }
        }

        private static string CreateWebRoot()
        {
            var root = Path.Combine(Path.GetTempPath(), "beacon-ui-redirect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "beacon", "assets"));
            File.WriteAllText(Path.Combine(root, "beacon", "index.html"), $"<!doctype html>{IndexMarker}");
            File.WriteAllText(Path.Combine(root, "beacon", "assets", "app.js"), AssetBody);

            return root;
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
                .ReturnsAsync(new ApiKeyCredential
                {
                    Id = 1,
                    UserId = 7,
                    Name = "redirect",
                    KeyHash = "hash",
                    KeyPrefix = ExecuteKey[..12],
                    Scopes = "[\"Execute\"]",
                    User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true }
                });

            return apiKeys;
        }
    }
}
