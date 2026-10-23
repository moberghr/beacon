using System.Net;
using System.Security.Claims;
using System.Text;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.Mcp;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.MCP;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The request limits of Beacon:RateLimits: per caller on /beacon/api (an anonymous caller by its address), per address on
/// the anonymous requests Beacon refuses with 401, per API key or caller on the MCP route, and per caller on the ask tool.
/// Over a limit the answer is 429 as a problem with Retry-After (the ask tool answers an audited tool error).
/// </summary>
[TestFixture]
public class RequestRateLimitTests
{
    private const string TestUserHeader = "X-Test-User";
    private const string TestIpHeader = "X-Test-Ip";

    [Test]
    public async Task BeaconApi_AnonymousCaller_IsLimitedPerAddress()
    {
        await using var app = await StartBeaconApiAsync(new RateLimitOptions { Api = new RateLimitWindow { PermitLimit = 2 } });
        using var client = app.GetTestClient();

        (await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.OK);
        var throttled = await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.1");
        var otherAddress = await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.2");

        await ShouldBeTooManyRequestsAsync(throttled);
        otherAddress.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task BeaconApi_SignedInCaller_IsLimitedPerUser_WhateverItsAddress()
    {
        await using var app = await StartBeaconApiAsync(new RateLimitOptions { Api = new RateLimitWindow { PermitLimit = 2 } });
        using var client = app.GetTestClient();

        await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.1", user: "ext-ana");
        await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.2", user: "ext-ana");
        var throttled = await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.3", user: "ext-ana");
        var otherUser = await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.1", user: "ext-ben");

        await ShouldBeTooManyRequestsAsync(throttled);
        otherUser.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Test]
    public async Task BeaconApi_LimitOff_NeverThrottles()
    {
        await using var app = await StartBeaconApiAsync(new RateLimitOptions { Api = new RateLimitWindow { PermitLimit = 0 } });
        using var client = app.GetTestClient();

        for (var attempt = 0; attempt < 20; attempt++)
        {
            (await GetAsync(client, "/beacon/api/auth/me", ip: "10.0.0.1")).StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Test]
    public async Task RefusedApiKeys_AreLimitedPerAddress_OnBeaconRoutes()
    {
        var limiter = new BeaconApiRateLimiter(Options.Create(new RateLimitOptions { Anonymous = new RateLimitWindow { PermitLimit = 2 } }));

        var first = await RefusedKeyAsync(limiter, "10.0.0.1");
        var second = await RefusedKeyAsync(limiter, "10.0.0.1");
        var third = await RefusedKeyAsync(limiter, "10.0.0.1");
        var otherAddress = await RefusedKeyAsync(limiter, "10.0.0.2");

        first.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        second.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        third.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        third.Response.Headers.RetryAfter.ToString().Should().MatchRegex("^[0-9]+$");
        third.Response.ContentType.Should().StartWith("application/problem+json");
        otherAddress.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Test]
    public void AnonymousRefusals_OutsideBeaconRoutes_AreNotCounted()
    {
        var limiter = new BeaconApiRateLimiter(Options.Create(new RateLimitOptions { Anonymous = new RateLimitWindow { PermitLimit = 1 } }));
        var services = new ServiceCollection().AddSingleton(limiter).BuildServiceProvider();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.Path = "/host/api/orders";

            AnonymousRefusalThrottle.TryAdmit(context, out _).Should().BeTrue();
        }
    }

    [Test]
    public async Task McpRoute_IsLimitedPerApiKey()
    {
        await using var app = await StartMcpAsync(new RateLimitOptions { Mcp = new RateLimitWindow { PermitLimit = 2 } });
        using var client = app.GetTestClient();

        (await PostMcpAsync(client, apiKeyId: "3")).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        (await PostMcpAsync(client, apiKeyId: "3")).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        var throttled = await PostMcpAsync(client, apiKeyId: "3");
        var otherKey = await PostMcpAsync(client, apiKeyId: "4");

        await ShouldBeTooManyRequestsAsync(throttled);
        otherKey.StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public async Task AskTool_IsLimitedPerCaller_WithAnAuditedRefusal()
    {
        var limiter = new McpRateLimiter(Options.Create(new RateLimitOptions { Ask = new RateLimitWindow { PermitLimit = 1 } }));
        var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();

        var first = await AskAsync(limiter, apiKeyId: 3, auditFactory.Object);
        var second = await AskAsync(limiter, apiKeyId: 3, auditFactory.Object);
        var otherKey = await AskAsync(limiter, apiKeyId: 4, auditFactory.Object);

        TextOf(first).Should().NotContain("Too many questions");
        (second.IsError ?? false).Should().BeTrue();
        TextOf(second).Should().MatchRegex("Too many questions for this credential; retry in [0-9]+ seconds\\.");
        TextOf(otherKey).Should().NotContain("Too many questions");
        auditFactory.Verify(
            x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(3),
            "every call, the refused one included, writes its audit row");
    }

    private static async Task ShouldBeTooManyRequestsAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await response.Content.ReadAsStringAsync()).Should().Contain("\"status\":429");
    }

    private static Task<HttpResponseMessage> GetAsync(HttpClient client, string path, string ip, string? user = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add(TestIpHeader, ip);
        if (user != null)
        {
            request.Headers.Add(TestUserHeader, user);
        }

        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> PostMcpAsync(HttpClient client, string apiKeyId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, McpDiscoveryPaths.McpPath)
        {
            Content = new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json")
        };
        request.Headers.Add(TestUserHeader, apiKeyId);

        return client.SendAsync(request);
    }

    private static async Task<HttpContext> RefusedKeyAsync(BeaconApiRateLimiter limiter, string ip)
    {
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Beacon.Core.Data.Entities.ApiKeyCredential?)null);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddLogging().AddProblemDetails().AddSingleton(limiter).BuildServiceProvider()
        };
        context.Request.Path = "/beacon/api/queries";
        context.Request.Headers.Authorization = "Bearer sk-sem_unknown-key";
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        context.Response.Body = new MemoryStream();

        await new ApiKeyAuthMiddleware(_ => Task.CompletedTask).InvokeAsync(context, apiKeys.Object, NullLogger<ApiKeyAuthMiddleware>.Instance);

        return context;
    }

    // The ask tool without an LLM provider: a call that passes the limit answers "AI features not configured".
    private static async Task<ModelContextProtocol.Protocol.CallToolResult> AskAsync(
        McpRateLimiter limiter,
        int apiKeyId,
        IDbContextFactory<BeaconContext> auditFactory)
    {
        var settingsProvider = SettingsProviderMock.Create();
        var serviceProvider = new Mock<IServiceProvider>();
        serviceProvider.Setup(x => x.GetService(typeof(McpRateLimiter))).Returns(limiter);
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        var tool = new ProjectAskTool(
            Mock.Of<IKnowledgeGraphService>(),
            settingsProvider.Object,
            SettingsProviderMock.Unshared(settingsProvider.Object),
            serviceProvider.Object,
            new McpProjectContext { UserId = 1, ApiKeyId = apiKeyId, AllowedProjectIds = [7], QueryProjectIds = [7] },
            new McpAuditService(auditFactory, SettingsProviderMock.Create().Object, new HttpContextAccessor(), Options.Create(new McpDeploymentOptions()), NullLogger<McpAuditService>.Instance, new McpAuditOutcome(), Options.Create(new BeaconTelemetryOptions()), NullLoggerFactory.Instance, TestMcpAudit.ContentHasher, new McpStatementLog()),
            new McpSignalService(factory.Object, settingsProvider.Object, NullLogger<McpSignalService>.Instance),
            Mock.Of<IAskSqlPipeline>(),
            Mock.Of<IAskSqlExecutor>(),
            Mock.Of<IIntentClassifier>(),
            Mock.Of<IDataSourceRouter>(),
            Mock.Of<IKnowledgeAnswerService>(),
            Mock.Of<ICrossSourceQueryService>(),
            Options.Create(new McpDeploymentOptions()),
            new McpQueryConcurrencyLimiter(),
            TimeProvider.System,
            NullLogger<ProjectAskTool>.Instance);

        return await tool.ExecuteAsync("How many orders were placed?", cancellationToken: CancellationToken.None);
    }

    private static string TextOf(ModelContextProtocol.Protocol.CallToolResult result)
    {
        return string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(x => x.Text));
    }

    private static async Task<WebApplication> StartBeaconApiAsync(RateLimitOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new BeaconConfiguration());
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false });
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddSingleton(Mock.Of<IBeaconAuthorizationProvider>());
        builder.Services.AddSingleton(TestPermissions.None);
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddAntiforgery();
        builder.Services.AddSingleton(Options.Create(options));
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddWebRequestPrincipal();
        builder.Services
            .AddAuthentication("Test")
            .AddCookie("Test", x => x.Events.OnRedirectToLogin = y =>
            {
                y.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            });

        var app = builder.Build();
        app.Use(TestCaller(apiKey: false));
        app.UseAuthorization();
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }

    private static async Task<WebApplication> StartMcpAsync(RateLimitOptions options)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddWebRequestPrincipal();
        builder.Services.AddSingleton(new McpRateLimiter(Options.Create(options)));
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, StatusCodeResultHandler>();

        var app = builder.Build();
        app.Use(TestCaller(apiKey: true));
        app.UseRouting();
        app.UseAuthorization();
        app.MapBeaconMcp();
        await app.StartAsync();

        return app;
    }

    // Stands in for the host's address handling and Beacon's authentication middlewares: the test address and caller are
    // set before UseAuthorization runs. A caller is a signed-in user, or an Execute-scoped API key of user 7.
    private static Func<HttpContext, RequestDelegate, Task> TestCaller(bool apiKey)
    {
        return (context, next) =>
        {
            if (context.Request.Headers.TryGetValue(TestIpHeader, out var ip))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            }

            if (context.Request.Headers.TryGetValue(TestUserHeader, out var caller))
            {
                Claim[] claims = apiKey
                    ?
                    [
                        new(ClaimTypes.NameIdentifier, "7"),
                        new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
                        new(McpCallerClaimTypes.ApiKeyId, caller.ToString()),
                        new(McpCallerClaimTypes.Scope, "Execute"),
                        new(McpCallerClaimTypes.AllowedProjects, "[7]")
                    ]
                    : [new(ClaimTypes.NameIdentifier, caller.ToString())];
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, apiKey ? "ApiKey" : "Cookies"));
            }

            return next(context);
        };
    }

    private sealed class StatusCodeResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Succeeded)
            {
                return next(context);
            }

            context.Response.StatusCode = authorizeResult.Challenged ? StatusCodes.Status401Unauthorized : StatusCodes.Status403Forbidden;

            return Task.CompletedTask;
        }
    }
}
