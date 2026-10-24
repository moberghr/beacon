using System.Net;
using System.Text.Json;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.OpenApi;
using Beacon.Core.Authentication;
using Beacon.Core.Data.Enums;
using Beacon.Gateway;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Hosting;

/// <summary>
/// The host helpers in Beacon.Api that replace wiring the sample host used to copy: the long-running timeouts (§6.3), the
/// OIDC bearer JWT setup for MCP clients, the OpenAPI document with named enum schemas, and the RFC 7807 exception
/// mapping <c>UseBeaconPipeline</c> adds for <c>/beacon/api</c>.
/// </summary>
[TestFixture]
public class BeaconApiHostHelpersTests
{
    private const string OidcSection = "Beacon:Authentication:Oidc";

    [Test]
    public async Task ConfigureBeaconTimeouts_SetsKestrelAndHttpClientTimeouts_ToFiveMinutes()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.ConfigureBeaconTimeouts();
        await using var app = builder.Build();

        var kestrel = app.Services.GetRequiredService<IOptions<KestrelServerOptions>>().Value;
        using var client = app.Services.GetRequiredService<IHttpClientFactory>().CreateClient("any");

        BeaconHostTimeouts.LongRunningTimeout.Should().Be(TimeSpan.FromMinutes(5));
        kestrel.Limits.KeepAliveTimeout.Should().Be(TimeSpan.FromMinutes(5));
        kestrel.Limits.RequestHeadersTimeout.Should().Be(TimeSpan.FromMinutes(5));
        client.Timeout.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Test]
    public void AddBeaconOidcMcpJwtAuthentication_WithSsoAndAJwksEndpoint_ValidatesTheProvidersTokens()
    {
        var services = new ServiceCollection();

        services.AddBeaconOidcMcpJwtAuthentication(Oidc(enabled: true, jwksEndpoint: "https://login.example.test/tenant/keys"));

        var options = services.Single(x => x.ServiceType == typeof(JwtAuthenticationOptions)).ImplementationInstance
            .Should().BeOfType<JwtAuthenticationOptions>().Subject;
        options.EnableBearerAuthentication.Should().BeTrue();
        options.Validation.JwksEndpoint.Should().Be("https://login.example.test/tenant/keys");
        options.Validation.ValidIssuer.Should().Be("https://login.example.test/tenant/v2.0", "the trailing slash is trimmed");
        options.Validation.ValidAudience.Should().Be("beacon-client");
        options.Validation.ValidateIssuer.Should().BeTrue();
        options.Validation.ValidateAudience.Should().BeTrue();
    }

    [TestCase(false, "https://login.example.test/tenant/keys")]
    [TestCase(true, null)]
    [TestCase(true, " ")]
    public void AddBeaconOidcMcpJwtAuthentication_WithoutSsoOrWithoutAJwksEndpoint_RegistersNothing(bool enabled, string? jwksEndpoint)
    {
        var services = new ServiceCollection();

        services.AddBeaconOidcMcpJwtAuthentication(Oidc(enabled, jwksEndpoint));

        services.Should().BeEmpty();
    }

    [Test]
    public async Task AddBeaconOpenApi_NamesEnumSchemasAfterTheirType_WithTheirValuesAndNames()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddBeaconOpenApi();
        await using var app = builder.Build();
        app.MapGet("/sample", () => new SampleResponse(ApprovalStatus.Approved, null));
        app.MapOpenApi();
        await app.StartAsync();

        using var response = await app.GetTestClient().GetAsync("/openapi/v1.json");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var schema = document.RootElement.GetProperty("components").GetProperty("schemas").GetProperty(nameof(ApprovalStatus));
        schema.GetProperty("enum").EnumerateArray().Select(x => x.GetInt32())
            .Should().Equal(Enum.GetValues<ApprovalStatus>().Select(x => (int)x));
        schema.GetProperty("x-enumNames").EnumerateArray().Select(x => x.GetString())
            .Should().Equal(Enum.GetNames<ApprovalStatus>());
    }

    [Test]
    public async Task UseBeaconPipeline_MapsAnExceptionUnderTheBeaconApi_ToAProblemResponse()
    {
        await using var app = await ChainHostAsync();

        using var response = await app.GetTestClient().GetAsync("/beacon/api/sample-failure");
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        problem.RootElement.GetProperty("type").GetString().Should().Be("/errors/invalid-operation");
        problem.RootElement.GetProperty("title").GetString().Should().Be("Sample rule failed.");
        problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
    }

    [Test]
    public async Task UseBeaconPipeline_LeavesAnExceptionOnAHostRoute_ToTheHost()
    {
        await using var app = await ChainHostAsync();

        var send = () => app.GetTestClient().GetAsync("/reports/sample-failure");

        (await send.Should().ThrowAsync<InvalidOperationException>()).WithMessage("Sample rule failed.");
    }

    private static async Task<WebApplication> ChainHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(BeaconHostChain.RequiredSettings());
        BeaconHostChain.Add(builder.Services, builder.Configuration);
        var app = builder.Build();
        app.UseBeaconPipeline();
        app.MapGet("/beacon/api/sample-failure", string () => throw new InvalidOperationException("Sample rule failed.")).AllowAnonymous();
        app.MapGet("/reports/sample-failure", string () => throw new InvalidOperationException("Sample rule failed."));
        await app.StartAsync();

        return app;
    }

    private static IConfiguration Oidc(bool enabled, string? jwksEndpoint)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{OidcSection}:Enabled"] = enabled ? "true" : "false",
                [$"{OidcSection}:Authority"] = "https://login.example.test/tenant/v2.0/",
                [$"{OidcSection}:ClientId"] = "beacon-client",
                [$"{OidcSection}:McpJwksEndpoint"] = jwksEndpoint
            })
            .Build();
    }

    private sealed record SampleResponse(ApprovalStatus Status, ApprovalStatus? Previous);
}
