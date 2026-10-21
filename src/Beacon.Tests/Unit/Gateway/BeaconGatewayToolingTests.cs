using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NUnit.Framework;
using Beacon.Core.PostgreSql;
using Beacon.Doctor;
using Beacon.Gateway;
using Beacon.Testing;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// The tooling a gateway host and its operators use: the test package lets a host's tests connect a real MCP client with
/// Entra-shaped tokens and in-memory people, and <c>beacon-doctor</c> reports a deployment's sign-in path the way an MCP
/// client walks it — passing a correct gateway, and failing one whose host answers the MCP route with its own sign-in.
/// </summary>
[TestFixture]
public class BeaconGatewayToolingTests
{
    private const string TenantId = "11111111-2222-3333-4444-555555555555";
    private const string ClientId = "66666666-7777-8888-9999-000000000000";
    private const string Ada = "0000000a-0000-0000-0000-000000000001";

    private static readonly string EncryptionKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    private static readonly Uri Endpoint = new("http://localhost/beacon/mcp");

    [Test]
    public async Task ATestHost_ConnectsARealMcpClient_AsAPersonItKnows()
    {
        var tokens = new McpTestTokens(TenantId, ClientId);
        var users = new McpTestUsers().Add(Ada, "42", "Ada Lovelace", new Claim("app_user_id", "42"));
        await using var app = await StartGatewayAsync(tokens, users);

        await using var client = await McpTestClient.ConnectAsync(app.GetTestClient(), tokens.For(Ada));

        client.Should().NotBeNull("initialize succeeded: the token is a caller");
    }

    [Test]
    public async Task APersonTheHostRemoved_CannotConnect()
    {
        var tokens = new McpTestTokens(TenantId, ClientId);
        var users = new McpTestUsers().Add(Ada, "42", "Ada Lovelace").Remove(Ada);
        await using var app = await StartGatewayAsync(tokens, users);

        var connect = () => McpTestClient.ConnectAsync(app.GetTestClient(), tokens.For(Ada));

        await connect.Should().ThrowAsync<Exception>("an unmapped caller is refused at the MCP route");
    }

    [Test]
    public async Task TheDoctor_PassesACorrectGateway_AndWarnsOnlyAboutWhatEntraDoesNotAdvertise()
    {
        var tokens = new McpTestTokens(TenantId, ClientId);
        await using var app = await StartGatewayAsync(tokens, new McpTestUsers().Add(Ada, "42", "Ada Lovelace"));
        using var http = new HttpClient(new EntraStandIn(app.GetTestServer().CreateHandler()));

        var results = await new McpDoctor(http).RunAsync(Endpoint, tokens.For(Ada), CancellationToken.None);
        var byCheck = results.ToDictionary(x => x.Check, x => x.Status);

        byCheck["challenge"].Should().Be(CheckStatus.Pass);
        byCheck["resource"].Should().Be(CheckStatus.Pass);
        byCheck["scopes"].Should().Be(CheckStatus.Pass);
        byCheck["authorization-servers"].Should().Be(CheckStatus.Pass);
        byCheck["issuer"].Should().Be(CheckStatus.Pass);
        byCheck["signing-keys"].Should().Be(CheckStatus.Pass);
        byCheck["pkce"].Should().Be(CheckStatus.Warn, "Entra supports S256 without advertising it");
        byCheck["absent-documents"].Should().Be(CheckStatus.Pass);
        byCheck["initialize"].Should().Be(CheckStatus.Pass);
    }

    [Test]
    public async Task TheDoctor_FailsAHostWhoseSignInAnswersTheMcpRoute()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapPost("/beacon/mcp", () => Results.Redirect("/Account/SignIn"));
        await app.StartAsync();

        var results = await new McpDoctor(app.GetTestClient()).RunAsync(Endpoint, token: null, CancellationToken.None);

        results.Should().Contain(x => x.Check == "challenge" && x.Status == CheckStatus.Fail && x.Detail.Contains("not 401"));
    }

    private static async Task<WebApplication> StartGatewayAsync(McpTestTokens tokens, McpTestUsers users)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Beacon:EncryptionKey"] = EncryptionKey,
            ["Beacon:PublicBaseUrl"] = "http://localhost",
            ["AzureAd:TenantId"] = TenantId,
            ["AzureAd:ClientId"] = ClientId
        });
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization();
        builder.Services.AddBeaconMcpGateway(builder.Configuration, x =>
        {
            x.ProjectName = "orders";
            x.Database(db => db.UsePostgreSql(NpgsqlDataSource.Create("Host=localhost;Database=unused")));
            x.Entra(builder.Configuration.GetSection("AzureAd"));
            x.Users<McpTestUsers>();
        });
        builder.Services.TrustMcpTestTokens(tokens);
        builder.Services.UseMcpTestUsers(users);
        builder.Services.UseMcpTestHostProject(7);

        var app = builder.Build();
        app.UseBeaconMcpGateway();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapBeaconMcpGateway();
        await app.StartAsync();

        return app;
    }

    /// <summary>Microsoft Entra's metadata and keys for the tenant, without the network; everything else goes to the test server.</summary>
    private sealed class EntraStandIn(HttpMessageHandler testServer) : DelegatingHandler(testServer)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host != "login.microsoftonline.com")
            {
                return base.SendAsync(request, cancellationToken);
            }

            var body = request.RequestUri.AbsolutePath.EndsWith("/openid-configuration", StringComparison.Ordinal)
                ? $$"""{"issuer":"https://login.microsoftonline.com/{{TenantId}}/v2.0","jwks_uri":"https://login.microsoftonline.com/{{TenantId}}/discovery/v2.0/keys"}"""
                : """{"keys":[]}""";

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
