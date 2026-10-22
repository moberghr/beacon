using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.PostgreSql;
using Beacon.Core.Services;
using Beacon.Gateway;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// The gateway's admin UI: only the gateway's part of Beacon's REST API is served, every served route needs the host's
/// admin policy on top of Beacon's own (Beacon's anonymous ones stay anonymous so a signed-out shell finds the host's
/// sign-in), and a holder of that policy — and nobody else, whatever role claims the host's sign-in adds — becomes
/// Beacon's administrator through a Beacon user named after the host's own user id. The host's claims transformations
/// still run, first.
/// </summary>
[TestFixture]
public class BeaconMcpGatewayAdminTests
{
    private const string AdminPolicy = "perm:beacon.admin";
    private const string SessionHeader = "X-Host-Session";

    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Test]
    public async Task EveryServedEndpoint_IsMapped_AndEveryOtherBeaconEndpoint_Answers404()
    {
        await using var app = await StartAsync();
        var mapped = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith("/beacon/api", StringComparison.OrdinalIgnoreCase) == true)
            .Select(x => BeaconGatewayApiProfile.KeyOf(x.RoutePattern.RawText, x.Metadata))
            .OfType<string>()
            .ToHashSet();

        mapped.Should().Contain(BeaconGatewayApiProfile.Allowed, "a Beacon upgrade that renames a served route must fail here, not 404 in the UI");
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/data-sources", session: "admin")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await SendAsync(app, HttpMethod.Post, "/beacon/api/mcp/tools/run", session: "admin")).StatusCode.Should().Be(HttpStatusCode.NotFound, "the playground would run a tool as the administrator");
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/api-keys", session: "admin")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AServedRoute_NeedsTheHostsAdminPolicy()
    {
        await using var app = await StartAsync();

        var signedOut = await SendAsync(app, HttpMethod.Get, "/beacon/api/mcp/audit", session: null);
        var withoutThePolicy = await SendAsync(app, HttpMethod.Get, "/beacon/api/mcp/audit", session: "employee");

        signedOut.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signedOut.Headers.Location!.ToString().Should().StartWith("/login", "the host's own challenge");
        withoutThePolicy.StatusCode.Should().Be(HttpStatusCode.Redirect);
        withoutThePolicy.Headers.Location!.ToString().Should().StartWith("/denied", "an Entra Admin role without the host's admin policy is nobody here");
    }

    [Test]
    public async Task BeaconsAnonymousEndpoints_StayAnonymous_SoASignedOutShellFindsTheHostsSignIn()
    {
        await using var app = await StartAsync();

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/auth/sso", session: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public void TheShell_IsToldItIsAGateway_AndWhereTheHostsSignInIs()
    {
        using var provider = Services().BuildServiceProvider();

        var api = provider.GetRequiredService<BeaconApiOptions>();

        api.GatewayMode.Should().BeTrue();
        api.Realtime.Should().BeFalse();
        api.ExternalLogin!.LoginUrl.Should().Be("/Account/SignIn");
    }

    [Test]
    public void TheToolCatalogue_ListsOnlyTheBuiltInToolsTheGatewayServes()
    {
        using var provider = Services().BuildServiceProvider();

        var tools = provider.GetRequiredService<IMcpPlaygroundService>().ToolNames;

        tools.Should().Equal(["get_context", "get_documentation", "search"], "a gateway serves no SQL tools, so the catalogue must not describe them");
    }

    [Test]
    public async Task AHolderOfTheHostPolicy_BecomesBeaconsAdministrator_AfterTheHostsOwnTransformation()
    {
        var users = Users();
        using var provider = Services(users).BuildServiceProvider();

        var principal = await TransformAsync(provider, "/beacon/api/mcp/audit", Session("admin"));

        principal.Identities.First().AuthenticationType.Should().Be(BeaconGatewayAdminClaimsTransformation.AuthenticationType);
        principal.FindFirst(ClaimTypes.NameIdentifier)!.Value.Should().Be("host:42", "Beacon names the session's user by its first NameIdentifier");
        principal.HasClaim("host_transformed", "yes").Should().BeTrue("the host's own transformation still runs");
        users.Verify(x => x.GetOrCreateExternalUserAsync("host:42", "host", "ada@example.com", "ada@example.com", "Ada", "Admin", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("employee", "/beacon/api/mcp/audit", "without the host's admin policy, an Entra Admin role grants nothing")]
    [TestCase("admin", "/beacon/mcp", "the MCP route is never an admin path")]
    [TestCase("admin", "/api/orders", "the host's own routes are the host's")]
    public async Task NobodyElse_AndNoOtherPath_GetsABeaconIdentity(string session, string path, string because)
    {
        var users = Users();
        using var provider = Services(users).BuildServiceProvider();

        var principal = await TransformAsync(provider, path, Session(session));

        principal.Identities.Should().NotContain(x => x.AuthenticationType == BeaconGatewayAdminClaimsTransformation.AuthenticationType, because);
        principal.HasClaim("host_transformed", "yes").Should().BeTrue();
        users.Verify(x => x.GetOrCreateExternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ABeaconUserDisabledInBeacon_GetsNoBeaconIdentity()
    {
        var users = Users();
        users
            .Setup(x => x.GetOrCreateExternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new BeaconException("User is disabled."));
        using var provider = Services(users).BuildServiceProvider();

        var principal = await TransformAsync(provider, "/beacon/api/mcp/audit", Session("admin"));

        principal.Identities.Should().NotContain(x => x.AuthenticationType == BeaconGatewayAdminClaimsTransformation.AuthenticationType, "Beacon may narrow what the host grants, never widen it");
    }

    [Test]
    public async Task AHostTransformationRegisteredAfterTheGateway_IsRefusedAtStartup()
    {
        var start = () => StartAsync(registerLateTransformation: true);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("Register your IClaimsTransformation before calling AddBeaconMcpGateway");
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = EncryptionKey,
                ["AzureAd:TenantId"] = "11111111-2222-3333-4444-555555555555",
                ["AzureAd:ClientId"] = "66666666-7777-8888-9999-000000000000"
            })
            .Build();

    private static void Configure(BeaconMcpGatewayOptions options, IConfiguration configuration)
    {
        options.ProjectName = "inventhor";
        options.Database(db => db.UsePostgreSql(NpgsqlDataSource.Create("Host=localhost;Database=unused")));
        options.Entra(configuration.GetSection("AzureAd"));
        options.Users<NoPeople>();
        options.AdminUi(x =>
        {
            x.HostPolicy = AdminPolicy;
            x.HostUserId = y => y.FindFirst("app_user_id")?.Value;
            x.SignInPath = "/Account/SignIn";
        });
    }

    private static ServiceCollection Services(Mock<IUserManagementService>? users = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = Configuration();
        services.AddSingleton(configuration);
        services.AddHttpContextAccessor();
        services.AddAuthorization(x => x.AddPolicy(AdminPolicy, y => y.RequireClaim("perm", "beacon.admin")));
        services.AddScoped<IClaimsTransformation, HostTransformation>();
        services.AddBeaconMcpGateway(configuration, x => Configure(x, configuration));
        if (users != null)
        {
            services.Replace(ServiceDescriptor.Scoped(_ => users.Object));
        }

        return services;
    }

    private static Mock<IUserManagementService> Users()
    {
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.GetOrCreateExternalUserAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BeaconUserData { Id = 5, ExternalId = "host:42", UserName = "ada@example.com", DisplayName = "Ada", Email = "ada@example.com" });

        return users;
    }

    private static async Task<ClaimsPrincipal> TransformAsync(ServiceProvider provider, string path, ClaimsPrincipal principal)
    {
        using var scope = provider.CreateScope();
        var httpContext = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        httpContext.Request.Path = path;
        scope.ServiceProvider.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        return await scope.ServiceProvider.GetRequiredService<IClaimsTransformation>().TransformAsync(principal);
    }

    private static ClaimsPrincipal Session(string who)
    {
        List<Claim> claims =
        [
            new("app_user_id", "42"),
            new(ClaimTypes.Name, "Ada"),
            new("name", "Ada"),
            new(ClaimTypes.Email, "ada@example.com"),
            new(ClaimTypes.Role, "Admin")
        ];
        if (who == "admin")
        {
            claims.Add(new Claim("perm", "beacon.admin"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "HostSession"));
    }

    private static async Task<WebApplication> StartAsync(bool registerLateTransformation = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddConfiguration(Configuration());
        builder.Services.AddAuthentication("HostSession").AddScheme<AuthenticationSchemeOptions, HostSessionHandler>("HostSession", _ => { });
        builder.Services.AddAuthorization(x => x.AddPolicy(AdminPolicy, y => y.RequireClaim("perm", "beacon.admin")));
        builder.Services.AddBeaconMcpGateway(builder.Configuration, x => Configure(x, builder.Configuration));
        if (registerLateTransformation)
        {
            builder.Services.AddScoped<IClaimsTransformation, HostTransformation>();
        }

        var hostProject = new Mock<IBeaconHostProjectGate>();
        hostProject.Setup(x => x.GetProjectIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(7);
        builder.Services.Replace(ServiceDescriptor.Singleton(hostProject.Object));

        // No database: the bridge provisions the administrator through a stand-in user store.
        var users = Users();
        builder.Services.Replace(ServiceDescriptor.Scoped(_ => users.Object));

        var app = builder.Build();
        app.UseBeaconMcpGateway();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBeaconMcpGateway();
        await app.StartAsync();

        return app;
    }

    private static Task<HttpResponseMessage> SendAsync(WebApplication app, HttpMethod method, string path, string? session)
    {
        var request = new HttpRequestMessage(method, path);
        if (session != null)
        {
            request.Headers.Add(SessionHeader, session);
        }

        return app.GetTestClient().SendAsync(request);
    }

    private sealed class NoPeople : IMcpHostUserResolver
    {
        public Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken cancellationToken) => Task.FromResult<McpHostUser?>(null);
    }

    /// <summary>The host's own transformation, registered before the gateway, as a host should.</summary>
    private sealed class HostTransformation : IClaimsTransformation
    {
        public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
        {
            var identity = new ClaimsIdentity([new Claim("host_transformed", "yes")]);

            return Task.FromResult(new ClaimsPrincipal(principal.Identities.Append(identity)));
        }
    }

    /// <summary>The host's sign-in: an admin or an employee session by header; challenge and forbid redirect as a cookie does.</summary>
    private sealed class HostSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(Request.Headers.TryGetValue(SessionHeader, out var who)
                ? AuthenticateResult.Success(new AuthenticationTicket(Session(who.ToString()), Scheme.Name))
                : AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/login");

            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.Redirect("/denied");

            return Task.CompletedTask;
        }
    }
}
