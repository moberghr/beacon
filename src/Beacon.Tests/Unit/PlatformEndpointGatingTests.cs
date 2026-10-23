using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Beacon.Api.Endpoints;
using Beacon.Api.OpenApi;
using Beacon.Core.Data;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The anonymous health route is throttled per IP, shares one database probe for a moment and names no cause to anonymous
/// callers; the OpenAPI document needs a signed-in caller outside Development unless the host publishes it.
/// </summary>
[TestFixture]
public class PlatformEndpointGatingTests
{
    [SetUp]
    public void ResetProbeCache()
    {
        HealthProbeCache.Reset();
    }

    [Test]
    public async Task Health_ForAnonymousCaller_Failure_NamesNoCause()
    {
        var (host, _) = await StartHealthHostAsync();
        await using var _host = host;
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/beacon/api/health");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        JsonDocument.Parse(body).RootElement.TryGetProperty("detail", out _).Should().BeFalse();
        body.Should().NotContain("connectivity");
    }

    [Test]
    public async Task Health_ForSignedInCaller_Failure_NamesTheCheck()
    {
        var (host, _) = await StartHealthHostAsync();
        await using var _host = host;
        using var client = host.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/beacon/api/health");
        request.Headers.Add("X-Test-User", "someone");

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        body.Should().Contain("Database connectivity check failed.");
    }

    [Test]
    public async Task Health_RepeatedRequests_ShareOneDatabaseProbe()
    {
        var (host, factory) = await StartHealthHostAsync();
        await using var _host = host;
        using var client = host.GetTestClient();

        for (var i = 0; i < 5; i++)
        {
            await client.GetAsync("/beacon/api/health");
        }

        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Health_CachedAnswers_AreNeverThrottled()
    {
        var (host, factory) = await StartHealthHostAsync();
        await using var _host = host;
        using var client = host.GetTestClient();
        HttpResponseMessage? last = null;

        for (var i = 0; i < 125; i++)
        {
            last = await client.GetAsync("/beacon/api/health");
        }

        last!.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Health_ProbesOverThePerIpLimit_Answer429WithRetryAfter()
    {
        HealthProbeCache.SetTtl(TimeSpan.Zero);
        var (host, _) = await StartHealthHostAsync();
        await using var _host = host;
        using var client = host.GetTestClient();
        HttpResponseMessage? last = null;

        for (var i = 0; i < 125; i++)
        {
            last = await client.GetAsync("/beacon/api/health");
        }

        last!.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        last.Headers.RetryAfter.Should().NotBeNull();
    }

    [Test]
    public async Task Health_ConcurrentRequestsAtExpiry_ShareOneProbe()
    {
        var (host, factory) = await StartHealthHostAsync();
        await using var _host = host;
        using var client = host.GetTestClient();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => client.GetAsync("/beacon/api/health")));

        factory.Verify(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task OpenApi_OutsideDevelopment_NeedsASignedInCaller()
    {
        await using var host = await StartOpenApiHostAsync(Environments.Production, anonymous: false);
        using var client = host.GetTestClient();
        using var signedIn = new HttpRequestMessage(HttpMethod.Get, "/openapi/v1.json");
        signedIn.Headers.Add("X-Test-User", "someone");

        var anonymousResponse = await client.GetAsync("/openapi/v1.json");
        var signedInResponse = await client.SendAsync(signedIn);

        anonymousResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        signedInResponse.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task OpenApi_OutsideDevelopment_WithAnonymousSetting_IsOpen()
    {
        await using var host = await StartOpenApiHostAsync(Environments.Production, anonymous: true);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task OpenApi_InDevelopment_IsOpen()
    {
        await using var host = await StartOpenApiHostAsync(Environments.Development, anonymous: false);
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/openapi/v1.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task<(WebApplication Host, Mock<IDbContextFactory<BeaconContext>> Factory)> StartHealthHostAsync()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("probe failed"));
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(factory.Object);
        var app = builder.Build();
        app.Use(SetTestUser);
        app.Use(UseUniqueRemoteIp());
        app
            .MapGroup("/beacon/api")
            .MapHealthEndpoints();
        await app.StartAsync();

        return (app, factory);
    }

    private static async Task<WebApplication> StartOpenApiHostAsync(string environment, bool anonymous)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Configuration["Beacon:OpenApi:Anonymous"] = anonymous ? "true" : "false";
        builder.Services.AddOpenApi();
        builder.Services
            .AddAuthentication("Cookies")
            .AddCookie(x => x.Events.OnRedirectToLogin = context =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                return Task.CompletedTask;
            });
        builder.Services.AddAuthorization(x => x.AddPolicy(
            BeaconApiEndpoints.AuthPolicyName,
            y => y.RequireAuthenticatedUser()));
        var app = builder.Build();
        app.Use(SetTestUser);
        app.UseAuthorization();
        app.MapBeaconOpenApi();
        await app.StartAsync();

        return app;
    }

    // The health limiter is process-wide and keyed by address: each host gets an address of its own.
    private static Func<HttpContext, RequestDelegate, Task> UseUniqueRemoteIp()
    {
        var count = Interlocked.Increment(ref _hostCount);
        var address = IPAddress.Parse($"10.1.{count / 250}.{count % 250}");

        return (context, next) =>
        {
            context.Connection.RemoteIpAddress = address;

            return next(context);
        };
    }

    private static int _hostCount;

    private static Task SetTestUser(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Headers.TryGetValue("X-Test-User", out var name))
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name.ToString())], "Test"));
        }

        return next(context);
    }
}
