using System.Security.Claims;
using System.Text;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Core.Configuration;
using Beacon.Core.Mcp;
using Beacon.MCP;
using Beacon.MCP.Services;
using Microsoft.AspNetCore.Routing;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.AspNetCore;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class RequestLimitsTests
{
    [Test]
    public void Options_DefaultsAreValid_NegativeValueFails()
    {
        var validator = new RequestLimitsOptionsValidator();

        validator.Validate(null, new RequestLimitsOptions()).Succeeded.Should().BeTrue();
        validator.Validate(null, new RequestLimitsOptions { McpMaxIdleSessions = -1 }).Failed.Should().BeTrue();
        validator.Validate(null, new RequestLimitsOptions { ApiMaxBodyBytes = 0, ApiTimeoutSeconds = 0 }).Succeeded.Should().BeTrue();
    }

    [TestCase("/beacon/api/projects", 2L * 1024 * 1024)]
    [TestCase("/beacon/mcp", 4L * 1024 * 1024)]
    [TestCase("/BEACON/API/queries", 2L * 1024 * 1024)]
    public async Task Middleware_SetsBodyLimitOnBeaconRoutes(string path, long expected)
    {
        var feature = new BodySizeFeature { MaxRequestBodySize = 30_000_000 };
        var context = CreateContext(path, feature);

        await new BeaconRequestLimitsMiddleware(_ => Task.CompletedTask, Options.Create(new RequestLimitsOptions()))
            .InvokeAsync(context);

        feature.MaxRequestBodySize.Should().Be(expected);
    }

    [Test]
    public async Task Middleware_KeepsAStricterHostLimit_AndLeavesHostRoutesAlone()
    {
        var strict = new BodySizeFeature { MaxRequestBodySize = 1000 };
        var host = new BodySizeFeature { MaxRequestBodySize = 30_000_000 };
        var middleware = new BeaconRequestLimitsMiddleware(_ => Task.CompletedTask, Options.Create(new RequestLimitsOptions()));

        await middleware.InvokeAsync(CreateContext("/beacon/api/projects", strict));
        await middleware.InvokeAsync(CreateContext("/host/upload", host));

        strict.MaxRequestBodySize.Should().Be(1000);
        host.MaxRequestBodySize.Should().Be(30_000_000);
    }

    [Test]
    public async Task Middleware_ZeroTurnsTheBodyLimitOff()
    {
        var feature = new BodySizeFeature { MaxRequestBodySize = 30_000_000 };

        await new BeaconRequestLimitsMiddleware(_ => Task.CompletedTask, Options.Create(new RequestLimitsOptions { ApiMaxBodyBytes = 0 }))
            .InvokeAsync(CreateContext("/beacon/api/projects", feature));

        feature.MaxRequestBodySize.Should().Be(30_000_000);
    }

    [Test]
    public async Task Middleware_AnswersGatewayTimeout_WhenAnApiRequestRunsPastTheTimeout()
    {
        var context = CreateContext("/beacon/api/slow", new BodySizeFeature());
        var middleware = new BeaconRequestLimitsMiddleware(
            x => Task.Delay(Timeout.Infinite, x.RequestAborted),
            Options.Create(new RequestLimitsOptions { ApiTimeoutSeconds = 1 }));

        await middleware.InvokeAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status504GatewayTimeout);
    }

    [Test]
    public async Task Middleware_DoesNotTimeOutARouteMarkedLongRunning()
    {
        var context = CreateContext("/beacon/api/slow", new BodySizeFeature());
        context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(BeaconLongRunningMetadata.Instance), "long"));
        var original = context.RequestAborted;
        var seen = default(CancellationToken);
        var middleware = new BeaconRequestLimitsMiddleware(
            async x =>
            {
                seen = x.RequestAborted;
                await Task.Delay(1500);
            },
            Options.Create(new RequestLimitsOptions { ApiTimeoutSeconds = 1 }));

        await middleware.InvokeAsync(context);

        seen.Should().Be(original);
        seen.IsCancellationRequested.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Test]
    public async Task Middleware_DoesNotTimeOutTheHub()
    {
        var context = CreateContext("/beacon/api/hub", new BodySizeFeature());
        var seen = default(CancellationToken);
        var original = context.RequestAborted;
        var middleware = new BeaconRequestLimitsMiddleware(
            x =>
            {
                seen = x.RequestAborted;

                return Task.CompletedTask;
            },
            Options.Create(new RequestLimitsOptions { ApiTimeoutSeconds = 1 }));

        await middleware.InvokeAsync(context);

        seen.Should().Be(original);
    }

    [Test]
    public void AddBeaconApiServices_AppliesHubBounds_AndAHostCanOverrideThem()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiServices(x => x.ConfigureSignalR = y => y.AddHubOptions<Microsoft.AspNetCore.SignalR.Hub>(_ => { }));
        services.Configure<HubOptions>(x => x.MaximumParallelInvocationsPerClient = 3);
        using var provider = services.BuildServiceProvider();

        var hub = provider.GetRequiredService<IOptions<HubOptions>>().Value;

        hub.MaximumReceiveMessageSize.Should().Be(8 * 1024);
        hub.ClientTimeoutInterval.Should().Be(TimeSpan.FromSeconds(30));
        hub.MaximumParallelInvocationsPerClient.Should().Be(3);
    }

    [Test]
    public void AddBeaconApiServices_DefaultsToOneParallelInvocation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiServices();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<HubOptions>>().Value.MaximumParallelInvocationsPerClient.Should().Be(1);
    }

    [Test]
    public void AddBeaconMcp_CapsIdleSessions_FromConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:RequestLimits:McpMaxIdleSessions"] = "250" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<RequestLimitsOptions>().Bind(configuration.GetSection(RequestLimitsOptions.SectionName));
        services.AddBeaconMcp();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IOptions<HttpServerTransportOptions>>().Value.MaxIdleSessionCount.Should().Be(250);
    }

    [Test]
    public void Limiter_RefusesPastTheLimit_PerCaller_AndRecoversAfterTheWindow()
    {
        var time = new FakeTimeProvider();
        var limiter = CreateLimiter(time, new RequestLimitsOptions { McpMaxInitializesPerCaller = 2, McpInitializeWindowSeconds = 60 });

        limiter.TryAcquire("key:1").Should().BeTrue();
        limiter.TryAcquire("key:1").Should().BeTrue();
        limiter.TryAcquire("key:1").Should().BeFalse();
        limiter.TryAcquire("key:2").Should().BeTrue();

        time.Advance(TimeSpan.FromSeconds(61));

        limiter.TryAcquire("key:1").Should().BeTrue();
    }

    [Test]
    public async Task Admit_CountsOnlyInitializeRequestsWithoutASession()
    {
        var limiter = CreateLimiter(new FakeTimeProvider(), new RequestLimitsOptions { McpMaxInitializesPerCaller = 1 });

        (await limiter.AdmitAsync(McpPost("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", withSession: false))).Should().BeTrue();
        (await limiter.AdmitAsync(McpPost("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""", withSession: false))).Should().BeTrue();
        (await limiter.AdmitAsync(McpPost("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""", withSession: true))).Should().BeTrue();
        (await limiter.AdmitAsync(McpPost("not json", withSession: false))).Should().BeTrue();

        (await limiter.AdmitAsync(McpPost("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""", withSession: false))).Should().BeTrue();
        var refused = McpPost("""{"jsonrpc":"2.0","id":2,"method":"initialize"}""", withSession: false);
        (await limiter.AdmitAsync(refused)).Should().BeFalse();

        refused.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        refused.Response.Headers.RetryAfter.ToString().Should().Be("60");
    }

    [Test]
    public async Task Admit_LeavesTheBodyReadableForTheSdk()
    {
        var limiter = CreateLimiter(new FakeTimeProvider(), new RequestLimitsOptions());
        var context = McpPost("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""", withSession: false);

        await limiter.AdmitAsync(context);

        using var reader = new StreamReader(context.Request.Body);
        (await reader.ReadToEndAsync()).Should().Contain("initialize");
    }

    [Test]
    public async Task Admit_ReadsOnlyABoundedPrefix_OfALargeBody()
    {
        var limiter = CreateLimiter(new FakeTimeProvider(), new RequestLimitsOptions { McpMaxInitializesPerCaller = 1 });
        var big = new string('x', 2 * 1024 * 1024);
        var body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"arguments":{"sql":"PAD"}}}""".Replace("PAD", big);
        var context = McpPost(body, withSession: false);
        var stream = new CountingStream(Encoding.UTF8.GetBytes(body));
        context.Request.Body = stream;

        (await limiter.AdmitAsync(context)).Should().BeTrue();
        (await limiter.AdmitAsync(McpPost("""{"jsonrpc":"2.0","id":1,"method":"initialize"}""", withSession: false))).Should().BeTrue();

        stream.BytesRead.Should().BeLessThan(64 * 1024);
    }

    [Test]
    public async Task Admit_CountsAnInitializeWhoseMethodFollowsAHugeParams()
    {
        var limiter = CreateLimiter(new FakeTimeProvider(), new RequestLimitsOptions { McpMaxInitializesPerCaller = 1 });
        var pad = new string('x', 20_000);
        var body = """{"params":{"pad":"PAD"},"jsonrpc":"2.0","id":1,"method":"initialize"}""".Replace("PAD", pad);

        (await limiter.AdmitAsync(McpPost(body, withSession: false))).Should().BeTrue();
        (await limiter.AdmitAsync(McpPost(body, withSession: false))).Should().BeFalse();
    }

    [TestCase("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{}}""", true)]
    [TestCase("""{"params":{"a":[1,{"b":2}]},"method":"initialize"}""", true)]
    [TestCase("""{"method":"tools/call","params":{}}""", false)]
    [TestCase("""{"params":{"method":"initialize"},"method":"ping"}""", false)]
    [TestCase("""[{"method":"initialize"}]""", false)]
    public void MayBeInitialize_ReadsTheTopLevelMethod(string json, bool expected)
    {
        McpInitializeLimiter.MayBeInitialize(Encoding.UTF8.GetBytes(json), complete: true).Should().Be(expected);
    }

    [Test]
    public void Limiter_SweepsAtMostOncePerWindow_AndNeverLosesALiveCount()
    {
        var time = new FakeTimeProvider();
        var limiter = CreateLimiter(time, new RequestLimitsOptions { McpMaxInitializesPerCaller = 1, McpInitializeWindowSeconds = 60 });

        for (var i = 0; i < 10_000; i++)
        {
            limiter.TryAcquire($"key:{i}").Should().BeTrue();
        }

        limiter.TryAcquire("key:new").Should().BeTrue();
        limiter.TryAcquire("key:5").Should().BeFalse();

        time.Advance(TimeSpan.FromSeconds(61));

        limiter.TryAcquire("key:5").Should().BeTrue();
    }

    [Test]
    public void Limiter_PastTheHardCap_NewCallersShareOneBucket()
    {
        var limiter = CreateLimiter(new FakeTimeProvider(), new RequestLimitsOptions { McpMaxInitializesPerCaller = 1, McpInitializeWindowSeconds = 60 });

        for (var i = 0; i < 20_000; i++)
        {
            limiter.TryAcquire($"key:{i}");
        }

        limiter.TryAcquire("key:fresh-a").Should().BeTrue();
        limiter.TryAcquire("key:fresh-b").Should().BeFalse();
    }

    private static McpInitializeLimiter CreateLimiter(TimeProvider time, RequestLimitsOptions options) =>
        new(Options.Create(options), NullLogger<McpInitializeLimiter>.Instance, time);

    private static DefaultHttpContext McpPost(string body, bool withSession)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = McpDiscoveryPaths.McpPath;
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (withSession)
        {
            context.Request.Headers["Mcp-Session-Id"] = "abc";
        }

        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(McpCallerClaimTypes.ApiKeyId, "7")], "ApiKey"));

        return context;
    }

    private static DefaultHttpContext CreateContext(string path, IHttpMaxRequestBodySizeFeature feature)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Features.Set(feature);

        return context;
    }

    private sealed class BodySizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class CountingStream(byte[] data) : MemoryStream(data)
    {
        public long BytesRead { get; private set; }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await base.ReadAsync(buffer, cancellationToken);
            BytesRead += count;

            return count;
        }
    }
}
