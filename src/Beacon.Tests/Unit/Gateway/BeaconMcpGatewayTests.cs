using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ModelContextProtocol.Server;
using Moq;
using Npgsql;
using NUnit.Framework;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;
using Beacon.Core.PostgreSql;
using Beacon.Core.Worker;
using Beacon.Gateway;
using Beacon.MCP.SavedQueries;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// <c>AddBeaconMcpGateway</c> composes Beacon as the MCP gateway of a host and nothing else: the host's users as callers,
/// the document tools, the read-only and no-content locks, Entra discovery defaults, no Beacon sign-in scheme and no
/// scheduler. On the wire, the MCP route answers 401/403 itself — never the host's sign-in redirect — and only to an MCP
/// credential: the host's session counts for nothing there, and a bearer token counts for nothing on the host's routes.
/// </summary>
[TestFixture]
public class BeaconMcpGatewayTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string ClientId = "66666666-7777-8888-9999-000000000000";
    private const string KnownPerson = "0000000a-0000-0000-0000-000000000001";
    private const string HostSessionHeader = "X-Host-Session";

    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    [Test]
    public void TheComposition_IsTheHostsUsers_DocumentTools_AndPinnedLocks_WithoutABeaconSignIn()
    {
        var services = Services();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<IMcpCallerMapper>().Should().BeOfType<HostUserMcpCallerMapper>();
        provider.GetRequiredService<IMcpHostPrincipalFactory>().Should().BeOfType<HostUserMcpHostPrincipalFactory>();
        provider.GetRequiredService<IBeaconScheduler>().Should().BeOfType<UnavailableBeaconScheduler>();
        provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!
            .Select(x => x.ProtocolTool.Name).Should().BeEquivalentTo("get_context", "search", "get_documentation");
        services.Should().NotContain(x => x.ServiceType == typeof(SavedQueryToolService));
        provider.GetRequiredService<Beacon.MCP.BeaconMcpOptions>().ServerDescription.Should().Be(BeaconMcpGatewayOptions.DefaultServerDescription);

        var locks = provider.GetRequiredService<IOptions<McpDeploymentOptions>>().Value;
        locks.ForceReadOnly.Should().BeTrue();
        locks.ForceNoContentRetention.Should().BeTrue();

        var jwt = provider.GetRequiredService<JwtAuthenticationOptions>();
        jwt.Validation.ValidIssuers.Should().Equal($"https://login.microsoftonline.com/{TenantId}/v2.0", $"https://sts.windows.net/{TenantId}/");
        jwt.Validation.ValidAudiences.Should().Equal($"api://{ClientId}", ClientId);
        jwt.Validation.JwksEndpoint.Should().Be($"https://login.microsoftonline.com/{TenantId}/discovery/v2.0/keys");

        provider.GetRequiredService<IAuthenticationSchemeProvider>().GetAllSchemesAsync().GetAwaiter().GetResult()
            .Select(x => x.Name).Should().Equal(BeaconMcpGatewayDefaults.AuthenticationScheme);
    }

    [Test]
    public void RetainingToolContent_LiftsOnlyTheContentLock()
    {
        using var provider = Services(x => x.RetainToolContent = true).BuildServiceProvider();

        var locks = provider.GetRequiredService<IOptions<McpDeploymentOptions>>().Value;

        locks.ForceReadOnly.Should().BeTrue();
        locks.ForceNoContentRetention.Should().BeFalse();
    }

    [Test]
    public void AGatewayWithoutItsEssentials_IsRefused_NamingWhatIsMissing()
    {
        var act = () => new ServiceCollection().AddBeaconMcpGateway(Configuration(), x => x.ProjectName = "inventhor");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Database(*")
            .And.Message.Should().Contain("Entra(").And.Contain("Users<TResolver>()");
    }

    [Test]
    public async Task WithoutAToken_TheMcpRouteAnswers401WithResourceMetadata_NotTheHostsRedirect()
    {
        await using var app = await StartAsync();

        var response = await PostInitializeAsync(app, token: null, hostSession: true);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the host's session counts for nothing on the MCP route");
        response.Headers.WwwAuthenticate.ToString().Should().Contain("resource_metadata=");
    }

    [Test]
    public async Task AMappedCaller_InitializesAnMcpSession()
    {
        await using var app = await StartAsync();

        var response = await PostInitializeAsync(app, Token(KnownPerson), hostSession: false);

        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    [Test]
    public async Task AMappedCaller_WithAHostSessionOnTheSameRequest_IsDecidedByItsToken()
    {
        await using var app = await StartAsync();

        var response = await PostInitializeAsync(app, Token(KnownPerson), hostSession: true);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the MCP route's policy evaluates the bearer caller, whatever the host's session");
    }

    [Test]
    public async Task ATokenOfAPersonTheHostDoesNotKnow_Is403_NotARedirect()
    {
        await using var app = await StartAsync();

        var response = await PostInitializeAsync(app, Token("0000000a-0000-0000-0000-000000000999"), hostSession: true);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task ABearerToken_AuthenticatesNobodyOnTheHostsRoutes()
    {
        await using var app = await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Get, "/api/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(KnownPerson));

        var response = await app.GetTestClient().SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect, "the host challenges as it always does: the token is not its credential");
    }

    [Test]
    public async Task TheDiscoveryDocument_SendsClientsToTheTenant_ForTheMcpScopeAndARefreshToken()
    {
        await using var app = await StartAsync();

        var document = JsonNode.Parse(await app.GetTestClient().GetStringAsync("/.well-known/oauth-protected-resource"))!;

        document["authorization_servers"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal($"https://login.microsoftonline.com/{TenantId}/v2.0");
        document["scopes_supported"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal($"api://{ClientId}/mcp.access", "offline_access");
    }

    [Test]
    public async Task ADocumentTheServerDoesNotServe_Is404_NotTheHostsPage()
    {
        await using var app = await StartAsync();

        var authorizationServer = await app.GetTestClient().GetAsync("/.well-known/oauth-authorization-server");
        var mcpSubPath = await app.GetTestClient().GetAsync("/beacon/mcp/anything");

        authorizationServer.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mcpSubPath.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task AHostThatReliedOnItsOnlySchemeBeingTheDefault_IsRefusedAtStartup()
    {
        var start = () => StartAsync(namedDefault: false);

        (await start.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Contain("services.AddAuthentication(\"HostSession\")");
    }

    [Test]
    public async Task TheStandaloneMigrator_RefusesASharedDataSourceThatWouldLeakTheSearchPath_BeforeConnecting()
    {
        await using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Multiplexing=true");

        var act = () => BeaconPostgreSqlMigrator.MigrateAsync(dataSource);

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("Multiplexing is on");
    }

    private static IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:EncryptionKey"] = EncryptionKey,
                ["Beacon:PublicBaseUrl"] = "https://inventhor.example.com",
                ["AzureAd:TenantId"] = TenantId,
                ["AzureAd:ClientId"] = ClientId
            })
            .Build();

    private static ServiceCollection Services(Action<BeaconMcpGatewayOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var configuration = Configuration();
        services.AddSingleton(configuration);
        services.AddBeaconMcpGateway(configuration, x => Configure(x, configuration, configure));

        return services;
    }

    private static void Configure(BeaconMcpGatewayOptions options, IConfiguration configuration, Action<BeaconMcpGatewayOptions>? more = null)
    {
        options.ProjectName = "inventhor";
        options.Database(db => db.UsePostgreSql(NpgsqlDataSource.Create("Host=localhost;Database=unused")));
        options.Entra(configuration.GetSection("AzureAd"));
        options.Users<KnownPeople>();
        more?.Invoke(options);
    }

    private static async Task<WebApplication> StartAsync(bool namedDefault = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddConfiguration(Configuration());

        var authentication = namedDefault ? builder.Services.AddAuthentication("HostSession") : builder.Services.AddAuthentication();
        authentication.AddScheme<AuthenticationSchemeOptions, HostSessionHandler>("HostSession", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddBeaconMcpGateway(builder.Configuration, x => Configure(x, builder.Configuration));

        // Signed with a test key instead of the tenant's keys; issuer and audience are checked as in production.
        var jwt = new JwtAuthenticationOptions { EnableBearerAuthentication = true };
        jwt.Validation.SigningKey = SigningKey;
        jwt.Validation.ValidIssuers = [$"https://login.microsoftonline.com/{TenantId}/v2.0"];
        jwt.Validation.ValidAudiences = [$"api://{ClientId}", ClientId];
        builder.Services.Replace(ServiceDescriptor.Singleton(jwt));
        var hostProject = new Mock<IBeaconHostProjectGate>();
        hostProject.Setup(x => x.GetProjectIdAsync(It.IsAny<CancellationToken>())).ReturnsAsync(7);
        builder.Services.Replace(ServiceDescriptor.Singleton(hostProject.Object));

        var app = builder.Build();
        app.UseBeaconMcpGateway();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/api/me", (ClaimsPrincipal user) => user.Identity!.Name).RequireAuthorization();
        app.MapBeaconMcpGateway();
        await app.StartAsync();

        return app;
    }

    private static Task<HttpResponseMessage> PostInitializeAsync(WebApplication app, string? token, bool hostSession)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/beacon/mcp")
        {
            Content = new StringContent(
                """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1"}}}""",
                Encoding.UTF8,
                "application/json")
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (hostSession)
        {
            request.Headers.Add(HostSessionHeader, "yes");
        }

        return app.GetTestClient().SendAsync(request);
    }

    private static string Token(string objectId)
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);
        Claim[] claims =
        [
            new("tid", TenantId),
            new("oid", objectId),
            new("sub", "pairwise-subject"),
            new("scp", "mcp.access"),
            new("azp", "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"),
            new("name", "Ada Lovelace")
        ];

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            $"https://login.microsoftonline.com/{TenantId}/v2.0", ClientId, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), credentials));
    }

    /// <summary>The host's users: one known person.</summary>
    private sealed class KnownPeople : IMcpHostUserResolver
    {
        public Task<McpHostUser?> FindAsync(McpTokenIdentity token, CancellationToken cancellationToken) =>
            Task.FromResult(token.ObjectId == KnownPerson
                ? new McpHostUser("42", "Ada Lovelace", [new Claim("app_user_id", "42")])
                : null);
    }

    /// <summary>The host's own sign-in: a session when the request says so; its challenge redirects, as a cookie scheme does.</summary>
    private sealed class HostSessionHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(HostSessionHeader))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "Ada"), new Claim(ClaimTypes.Role, "Admin")], Scheme.Name));

            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }

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
