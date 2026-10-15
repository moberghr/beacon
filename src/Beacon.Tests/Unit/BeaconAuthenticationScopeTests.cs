using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
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
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// Beacon's authentication acts on Beacon's routes only: <c>/beacon</c> and <c>Beacon:Authentication:AdditionalPaths</c>.
/// A minimal host wired with <c>UseBeaconAuthentication</c>, its own default authentication scheme and the login and
/// first-run redirects: the host's own <c>RequireAuthorization()</c> routes refuse a Beacon API key, the <c>Beacon.Auth</c>
/// cookie and a bearer token Beacon accepts, and are never redirected to Beacon's pages, while Beacon's routes and
/// <c>/warp</c> still authenticate all three. No database (§4.7).
/// </summary>
[TestFixture]
public class BeaconAuthenticationScopeTests
{
    private const string HostScheme = "Host";
    private const string ApiKey = "sk-sem_scope-test-key";
    private const string Issuer = "https://idp.example.test";
    private const string Audience = "api://beacon";
    private const string UserSub = "user-sub";

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    public enum Credential
    {
        ApiKey,
        Cookie,
        Jwt
    }

    [Test]
    public async Task HostRoute_RefusesABeaconCredential(
        [Values] Credential credential,
        [Values("/api/orders", "/beaconx/orders", "/warpx")] string path)
    {
        await using var host = await ScopeHost.StartAsync();

        var response = await host.SendAsync(path, credential);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"{path} is the host's route, and a Beacon {credential} is not the host's credential");
    }

    [Test]
    public async Task BeaconRoute_AuthenticatesTheCredential(
        [Values] Credential credential,
        [Values("/beacon/api/whoami", "/beacon/api/hub", "/beacon/mcp", "/warp", "/warp/jobs", "/Beacon/Api/WhoAmI", "/WARP/jobs")] string path)
    {
        await using var host = await ScopeHost.StartAsync();

        var response = await host.SendAsync(path, credential);

        response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} is Beacon's (a /warp prefix is listed in AdditionalPaths), matched ignoring case");
        (await response.Content.ReadAsStringAsync()).Should().Be(AuthenticationTypeOf(credential));
    }

    [Test]
    public async Task UnderAPathBase_TheRouteAfterTheBaseDecides([Values] Credential credential)
    {
        await using var host = await ScopeHost.StartAsync(pathBase: "/portal");

        (await host.SendAsync("/portal/beacon/api/whoami", credential)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.SendAsync("/portal/warp", credential)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.SendAsync("/portal/api/orders", credential)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task ApiKey_RunsBeforeTheCookie()
    {
        await using var host = await ScopeHost.StartAsync();
        var cookie = await host.SignInAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/beacon/api/whoami");
        request.Headers.Add("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");

        var response = await host.Client.SendAsync(request);

        (await response.Content.ReadAsStringAsync()).Should().Be(McpCallerClaimTypes.ApiKeyAuthenticationType);
    }

    [Test]
    public async Task HostRoute_IsNeverRedirectedToBeaconsLoginOrSetupPage()
    {
        await using var host = await ScopeHost.StartAsync(firstRun: true);

        var hostPage = await host.SendAsync("/reports", credential: null);
        var beaconPage = await host.SendAsync("/beacon/home", credential: null);
        var loginPage = await host.SendAsync("/beacon/login", credential: null);

        hostPage.StatusCode.Should().Be(HttpStatusCode.OK, "an anonymous host page is the host's to serve");
        hostPage.Headers.Location.Should().BeNull();
        beaconPage.StatusCode.Should().Be(HttpStatusCode.Redirect);
        beaconPage.Headers.Location!.OriginalString.Should().Be("/beacon/login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.Redirect);
        loginPage.Headers.Location!.OriginalString.Should().Be("/beacon/setup");
        host.Users.Verify(
            x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "only the Beacon request that reached the first-run check asks Beacon's database; a host route never does");
    }

    [Test]
    public void IsBeaconRequest_MatchesBeaconAndTheListedPrefixes_BySegment()
    {
        string[] listed = ["/warp/", " /reports/admin", "warp-no-slash", "/", "//", "/a?b"];

        Matches("/beacon", listed).Should().BeTrue();
        Matches("/beacon/", listed).Should().BeTrue();
        Matches("/BEACON/API/x", listed).Should().BeTrue();
        Matches("/warp", listed).Should().BeTrue("a trailing '/' on an entry is ignored");
        Matches("/Warp/jobs/1", listed).Should().BeTrue();
        Matches("/reports/admin/x", listed).Should().BeTrue("an entry is trimmed");
        Matches("/beaconx", listed).Should().BeFalse("prefixes match whole segments");
        Matches("/warpx", listed).Should().BeFalse();
        Matches("/reports", listed).Should().BeFalse();
        Matches("/api/beacon", listed).Should().BeFalse();
        Matches("/warp-no-slash", listed).Should().BeFalse("an entry that is not a path prefix is never matched");
        Matches("/orders", listed).Should().BeFalse("'/' never hands the host's routes to Beacon");
        Matches("/", listed).Should().BeFalse();
        Matches(string.Empty, listed).Should().BeFalse();
        Matches("/warp", null).Should().BeFalse("without the options only /beacon is Beacon's");
    }

    [Test]
    public void AdditionalPaths_BindFromConfiguration_AndDefaultToNone()
    {
        using var configured = BuildBeaconProvider(new Dictionary<string, string?>
        {
            ["Beacon:Authentication:AdditionalPaths:0"] = "/warp",
            ["Beacon:Authentication:AdditionalPaths:1"] = "/hangfire"
        });
        using var defaults = BuildBeaconProvider([]);

        configured.GetRequiredService<IStartupValidator>().Validate();
        configured.GetRequiredService<IOptions<AuthenticationPathOptions>>().Value.AdditionalPaths
            .Should().Equal("/warp", "/hangfire");
        defaults.GetRequiredService<IOptions<AuthenticationPathOptions>>().Value.AdditionalPaths.Should().BeEmpty();
    }

    [TestCase("/")]
    [TestCase("warp")]
    [TestCase("")]
    [TestCase("/warp?x=1")]
    public void AdditionalPaths_EntryThatIsNotAPathPrefix_FailsTheHostAtStartup(string entry)
    {
        using var provider = BuildBeaconProvider(new Dictionary<string, string?>
        {
            ["Beacon:Authentication:AdditionalPaths:0"] = "/warp",
            ["Beacon:Authentication:AdditionalPaths:1"] = entry
        });

        var act = () => provider.GetRequiredService<IStartupValidator>().Validate();

        act.Should().Throw<OptionsValidationException>().WithMessage("*Beacon:Authentication:AdditionalPaths:1*");
    }

    [Test]
    public async Task CookieAsTheDefaultScheme_IsWarnedAboutOutsideDevelopment()
    {
        var logs = await StartForWarningAsync("Production", hostDefaultScheme: false);

        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("default authentication scheme"));
    }

    [TestCase("Development", false)]
    [TestCase("Production", true)]
    public async Task CookieThatIsNotTheDefault_OrDevelopment_IsNotWarnedAbout(string environment, bool hostDefaultScheme)
    {
        var logs = await StartForWarningAsync(environment, hostDefaultScheme);

        logs.Entries.Should().NotContain(x => x.Message.Contains("default authentication scheme"));
    }

    private static string AuthenticationTypeOf(Credential credential)
    {
        return credential switch
        {
            Credential.ApiKey => McpCallerClaimTypes.ApiKeyAuthenticationType,
            Credential.Cookie => CookieAuthenticationDefaults.AuthenticationScheme,
            _ => "Bearer"
        };
    }

    private static bool Matches(string path, string[]? additionalPaths)
    {
        var services = new ServiceCollection();
        if (additionalPaths != null)
        {
            services.AddSingleton(Options.Create(new AuthenticationPathOptions { AdditionalPaths = [.. additionalPaths] }));
        }

        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.Path = path;

        return BeaconRequestPaths.IsBeaconRequest(context);
    }

    private static ServiceProvider BuildBeaconProvider(Dictionary<string, string?> settings)
    {
        // A throwaway encryption key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        settings["Beacon:EncryptionKey"] = TestEncryptionKey.Value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>())
            .UsePostgreSql("Host=localhost;Database=unused;Username=unused;Password=unused");

        return services.BuildServiceProvider();
    }

    private static async Task<LogRecorder> StartForWarningAsync(string environment, bool hostDefaultScheme)
    {
        var logs = new LogRecorder();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();

        // Development validates every registration on build; this host registers the cookie scheme alone, not Beacon's stores.
        builder.Host.UseDefaultServiceProvider(x => x.ValidateOnBuild = false);
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddBeaconCookieAuthentication("/beacon");
        if (hostDefaultScheme)
        {
            builder.Services.AddAuthentication(HostScheme)
                .AddScheme<AuthenticationSchemeOptions, HostAuthenticationHandler>(HostScheme, null);
        }

        await using var app = builder.Build();
        await app.StartAsync();
        await app.StopAsync();

        return logs;
    }

    private sealed class ScopeHost : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private ScopeHost(WebApplication app, Mock<IUserManagementService> users)
        {
            _app = app;
            Users = users;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public Mock<IUserManagementService> Users { get; }

        public static async Task<ScopeHost> StartAsync(string? pathBase = null, bool firstRun = false)
        {
            var configuration = new BeaconConfiguration();
            configuration.Authentication.EnableLoginForm = true;
            configuration.UserManagement.Enabled = true;
            var users = UserStore(firstRun);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:Authentication:AdditionalPaths:0"] = "/warp"
            });

            // As AddBeaconServices binds it.
            builder.Services.AddOptions<AuthenticationPathOptions>()
                .Bind(builder.Configuration.GetSection(AuthenticationPathOptions.SectionName));
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(users.Object);
            builder.Services.AddSingleton(ApiKeys().Object);
            builder.Services.AddSingleton(Mock.Of<IMcpCallerMapper>());
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

            // The host's own sign-in: its default scheme, which accepts no Beacon credential.
            builder.Services.AddAuthentication(x =>
                {
                    x.DefaultScheme = HostScheme;
                    x.DefaultChallengeScheme = HostScheme;
                    x.DefaultForbidScheme = HostScheme;
                })
                .AddScheme<AuthenticationSchemeOptions, HostAuthenticationHandler>(HostScheme, null);
            builder.Services.AddAuthorization();

            var app = builder.Build();
            if (pathBase != null)
            {
                app.UsePathBase(pathBase);
            }

            app.UseRouting();
            app.UseBeaconAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<LoginFormAuthMiddleware>(configuration, "/beacon");
            app.UseMiddleware<FirstRunSetupMiddleware>(configuration, "/beacon");

            // The host's routes.
            app.MapGet("/api/orders", WhoAmI).RequireAuthorization();
            app.MapGet("/beaconx/orders", WhoAmI).RequireAuthorization();
            app.MapGet("/warpx", WhoAmI).RequireAuthorization();
            app.MapGet("/reports", () => "reports");

            // Beacon's routes, and the Warp dashboard the host lists in AdditionalPaths.
            app.MapGet("/beacon/api/whoami", WhoAmI).RequireAuthorization();
            app.MapGet("/beacon/api/hub", WhoAmI).RequireAuthorization();
            app.MapGet("/beacon/mcp", WhoAmI).RequireAuthorization();
            app.MapGet("/beacon/{**page}", () => "spa");
            app.MapGet("/warp/{**page}", WhoAmI).RequireAuthorization();
            app.MapGet("/beacon/api/test/sign-in", async (HttpContext context) =>
            {
                var principal = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "ana"), new Claim(ClaimTypes.Name, "ana")],
                    CookieAuthenticationDefaults.AuthenticationScheme));
                await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
            });

            await app.StartAsync();

            return new ScopeHost(app, users);
        }

        public async Task<HttpResponseMessage> SendAsync(string path, Credential? credential)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            switch (credential)
            {
                case Credential.ApiKey:
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
                    break;
                case Credential.Cookie:
                    request.Headers.Add("Cookie", await SignInAsync());
                    break;
                case Credential.Jwt:
                    request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {MintToken()}");
                    break;
            }

            return await Client.SendAsync(request);
        }

        public async Task<string> SignInAsync()
        {
            var response = await Client.GetAsync("/beacon/api/test/sign-in");
            response.StatusCode.Should().Be(HttpStatusCode.OK);

            return response.Headers.GetValues("Set-Cookie")
                .Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal))
                .Split(';')[0];
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

        private static Mock<IUserManagementService> UserStore(bool firstRun)
        {
            var users = new Mock<IUserManagementService>();
            users
                .Setup(x => x.IsFirstRunAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(firstRun);
            users
                .Setup(x => x.GetBearerUserCandidatesAsync(UserSub, Issuer, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(
                [
                    new BearerUserCandidate
                    {
                        User = new BeaconUserData
                        {
                            Id = 42,
                            ExternalId = UserSub,
                            IdentityProvider = Issuer,
                            UserName = "ana",
                            DisplayName = "Ana Analyst",
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
                .Setup(x => x.ValidateApiKeyAsync(ApiKey, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ApiKeyCredential
                {
                    Id = 3,
                    UserId = 7,
                    Name = "scope",
                    KeyHash = "hash",
                    KeyPrefix = ApiKey[..12],
                    Scopes = "[\"Read\"]",
                    User = new BeaconUser { Id = 7, UserName = "ana", ExternalId = "ana", IsEnabled = true }
                });

            return apiKeys;
        }

        // An access token (typ at+jwt) from the configured issuer, naming an enabled external Beacon user.
        private static string MintToken()
        {
            var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
            var header = new JwtHeader(credentials);
            header[JwtHeaderParameterNames.Typ] = "at+jwt";
            var payload = new JwtPayload(
                Issuer,
                Audience,
                [new Claim("sub", UserSub)],
                notBefore: DateTime.UtcNow.AddMinutes(-1),
                expires: DateTime.UtcNow.AddMinutes(10));

            return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(header, payload));
        }
    }

    // The host's own scheme: it authenticates nothing a test sends, and its challenge is a plain 401.
    private sealed class HostAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
