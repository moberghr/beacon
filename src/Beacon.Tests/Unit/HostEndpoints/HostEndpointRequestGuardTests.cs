using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Beacon.Core.HostEndpoints;
using Beacon.MCP.HostEndpoints;
using Beacon.Tests.Common;
using static Beacon.Tests.Unit.HostEndpoints.HostEndpointDispatchTests;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// What a dispatched call must satisfy beyond the endpoint's own pipeline: a read-only tool uses a read verb, route
/// values pass the route's constraints and are values routing could deliver, and calls are bounded per caller and in
/// total, counting a call until its endpoint has really ended.
/// </summary>
[TestFixture]
public class HostEndpointRequestGuardTests
{
    private static readonly System.Security.Claims.Claim CanView = HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings);

    [Test]
    public async Task AReadOnlyPostEndpoint_IsNotExposed_WhenTheHostTurnsReadOnlyPostOff()
    {
        var logs = new LogRecorder();

        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x.AllowReadOnlyPost = false,
            services: x => x.AddSingleton<ILoggerProvider>(logs),
            map: x =>
            {
                x.MapPost("/guarded/search", ([FromForm] string term) => Results.Ok(new { term }))
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("guarded_search", "Search by a term.", readOnly: true);
                x.MapGet("/guarded/list", () => Results.Ok())
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("guarded_list", "List.", readOnly: true);
            });

        host.Registry.Find("guarded_search").Should().BeNull();
        host.Registry.Find("guarded_list").Should().NotBeNull();
        logs.Contains("[guarded_search]").Should().BeTrue();
        logs.Contains("AllowReadOnlyPost").Should().BeTrue();
    }

    [Test]
    public async Task AReadOnlyPostEndpoint_IsExposed_ByDefault_AndIsWarnedAtStartup()
    {
        var logs = new LogRecorder();

        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            services: x => x.AddSingleton<ILoggerProvider>(logs),
            map: x => x.MapPost("/guarded/search", ([FromForm] string term) => Results.Ok(new { term }))
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_search", "Search by a term.", readOnly: true));

        host.Tool("guarded_search").HttpMethod.Should().Be("POST");
        logs.Contains("[guarded_search]").Should().BeTrue();
        logs.Contains("AllowReadOnlyPost = false").Should().BeTrue("the warning says how to opt out");
    }

    [TestCase("PUT", true)]
    [TestCase("PATCH", true)]
    [TestCase("DELETE", true)]
    [TestCase("PUT", false)]
    [TestCase("DELETE", false)]
    public async Task AReadOnlyEndpointOnAWriteVerb_IsWarned_AndExposedOnlyWhileReadOnlyPostIsOn(string verb, bool allowReadOnlyPost)
    {
        var logs = new LogRecorder();

        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x.AllowReadOnlyPost = allowReadOnlyPost,
            services: x => x.AddSingleton<ILoggerProvider>(logs),
            map: x => x.MapMethods("/guarded/item/{id:int}", [verb], (int id) => Results.Ok(new { id }))
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_item", "One item.", readOnly: true));

        (host.Registry.Find("guarded_item") != null).Should().Be(allowReadOnlyPost);
        logs.Contains("[guarded_item]").Should().BeTrue();
        logs.Contains(verb).Should().BeTrue();
    }

    [Test]
    public async Task AReadOnlyEndpointThatAlsoTakesGet_IsExposedAsGet()
    {
        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x.AllowReadOnlyPost = false,
            map: x => x.MapMethods("/guarded/both", ["POST", "GET"], () => Results.Ok())
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_both", "Both verbs.", readOnly: true));

        host.Tool("guarded_both").HttpMethod.Should().Be("GET");
    }

    [Test]
    public async Task AnEndpointThatOnlyRequiresAnAuthenticatedUser_IsWarnedAtStartup()
    {
        var logs = new LogRecorder();

        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            services: x => x.AddSingleton<ILoggerProvider>(logs),
            map: x =>
            {
                x.MapGet("/guarded/any-user", () => Results.Ok())
                    .RequireAuthorization()
                    .WithBeaconTool("guarded_any_user", "Open to any signed-in user.", readOnly: true);
                x.MapGet("/guarded/with-policy", () => Results.Ok())
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("guarded_with_policy", "Needs a permission.", readOnly: true);
            });

        host.Registry.Find("guarded_any_user").Should().NotBeNull("the warning does not refuse the tool");
        var warnings = logs.Entries
            .Where(x => x.Level == LogLevel.Warning)
            .Select(x => x.Message)
            .Where(x => x.Contains("authenticated user"))
            .ToList();
        warnings.Should().ContainSingle().Which.Should().Contain("[guarded_any_user]");
    }

    [Test]
    public async Task ASelfServiceEndpoint_ThatBindsFromTheCaller_IsNotWarned()
    {
        var logs = new LogRecorder();

        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x.BindFromCaller("userId", "app_user_id"),
            services: x => x.AddSingleton<ILoggerProvider>(logs),
            map: x => x.MapGet("/guarded/my/orders", (int userId) => Results.Ok(new { userId }))
                .RequireAuthorization()
                .WithBeaconTool("guarded_my_orders", "The caller's orders.", readOnly: true));

        host.Registry.Find("guarded_my_orders").Should().NotBeNull();
        logs.Contains("authenticated user").Should().BeFalse();
    }

    [Test]
    public async Task ARouteValueThatFailsTheRouteConstraint_IsRefusedBeforeDispatch()
    {
        var runs = 0;
        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            map: x => x.MapGet("/guarded/reports/{name:regex(^[a-z0-9-]+$)}", (string name) =>
                {
                    Interlocked.Increment(ref runs);

                    return Results.Ok(new { name });
                })
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_report", "One report.", readOnly: true));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var refused = await service.CallAsync("api_guarded_report", Args(new { name = "Q1_Report" }), CancellationToken.None);
        var accepted = await service.CallAsync("api_guarded_report", Args(new { name = "q1-report" }), CancellationToken.None);

        refused.IsError.Should().BeTrue();
        Text(refused).Should().Contain("'name'").And.Contain("route constraint");
        accepted.IsError.Should().NotBe(true);
        accepted.StructuredContent!.Value.GetProperty("name").GetString().Should().Be("q1-report");
        runs.Should().Be(1);
        host.AuditLogs.Select(x => x.ErrorMessage).Should().Contain(x => x != null && x.Contains("route constraint"));
    }

    [Test]
    public async Task AnIntegerRouteConstraint_RefusesAValueThatIsNotAnInteger()
    {
        await using var host = await HostEndpointTestHost.StartAsync([typeof(ThingsController)], mapDefaultMinimalApis: false);
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var refused = await service.CallAsync("api_thing_by_id", Args(new { id = "two" }), CancellationToken.None);
        var accepted = await service.CallAsync("api_thing_by_id", Args(new { id = 2 }), CancellationToken.None);

        refused.IsError.Should().BeTrue();
        Text(refused).Should().Contain("'id'").And.Contain("route constraint");
        accepted.IsError.Should().NotBe(true);
    }

    [TestCase("../settings")]
    [TestCase("a/b")]
    [TestCase("..")]
    [TestCase(".")]
    public async Task ASingleSegmentRouteValue_RoutingCouldNotDeliver_IsRefused(string value)
    {
        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            map: x => x.MapGet("/guarded/files/{name}", (string name) => Results.Ok(new { name }))
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_file", "One file.", readOnly: true));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var result = await service.CallAsync("api_guarded_file", Args(new { name = value }), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("'name'");
    }

    [TestCase("reports/2026/q1.json", false)]
    [TestCase("reports/../secrets", true)]
    [TestCase("./reports", true)]
    public async Task ACatchAllRouteValue_TakesSlashes_ButNotDotSegments(string value, bool refused)
    {
        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            map: x => x.MapGet("/guarded/tree/{**path}", (string path) => Results.Ok(new { path }))
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_tree", "A file in the tree.", readOnly: true));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var result = await service.CallAsync("api_guarded_tree", Args(new { path = value }), CancellationToken.None);

        (result.IsError ?? false).Should().Be(refused, Text(result));
        if (!refused)
        {
            result.StructuredContent!.Value.GetProperty("path").GetString().Should().Be(value);
        }
    }

    [Test]
    public async Task ACallerAtItsLimit_IsRefused_UntilItsCallEnds()
    {
        var gate = new HeldEndpoint();
        await using var host = await StartHeldAsync(gate, x => x.MaxConcurrentCallsPerCaller = 1);
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var first = service.CallAsync("api_guarded_held", null, CancellationToken.None);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await service.CallAsync("api_guarded_held", null, CancellationToken.None);
        gate.Release.SetResult();
        var firstResult = await first;
        var third = await service.CallAsync("api_guarded_held", null, CancellationToken.None);

        second.IsError.Should().BeTrue();
        Text(second).Should().Contain("Too many host endpoint calls");
        firstResult.IsError.Should().NotBe(true);
        third.IsError.Should().NotBe(true);
        gate.Runs.Should().Be(2, "the refused call never reached the endpoint");
        host.AuditLogs.Select(x => x.ErrorMessage).Should().Contain(x => x != null && x.Contains("Too many host endpoint calls"));
    }

    [Test]
    public async Task TheTotalLimit_RefusesACall_WhenTheCallerHasSlotsLeft()
    {
        var gate = new HeldEndpoint();
        await using var host = await StartHeldAsync(gate, x =>
        {
            x.MaxConcurrentCalls = 1;
            x.MaxConcurrentCallsPerCaller = 4;
        });
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var first = service.CallAsync("api_guarded_held", null, CancellationToken.None);
        await gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = await service.CallAsync("api_guarded_held", null, CancellationToken.None);
        gate.Release.SetResult();
        await first;

        second.IsError.Should().BeTrue();
        Text(second).Should().Contain("Too many host endpoint calls");
    }

    [Test]
    public async Task ATimedOutCall_KeepsItsSlot_UntilTheEndpointEnds()
    {
        var gate = new HeldEndpoint();
        await using var host = await StartHeldAsync(gate, x =>
        {
            x.MaxConcurrentCallsPerCaller = 1;
            x.RequestTimeout = TimeSpan.FromMilliseconds(200);
        });
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(CanView)));

        var timedOut = await service.CallAsync("api_guarded_held", null, CancellationToken.None);
        var whileRunning = await service.CallAsync("api_guarded_held", null, CancellationToken.None);
        gate.Release.SetResult();
        var afterwards = await RetryWhileBusyAsync(() => service.CallAsync("api_guarded_held", null, CancellationToken.None));

        Text(timedOut).Should().Contain("timed out");
        Text(whileRunning).Should().Contain("Too many host endpoint calls");
        afterwards.IsError.Should().NotBe(true);
    }

    [Test]
    public void TheLimiter_CountsEachCallerApart_AndAllCallersTogether()
    {
        var limiter = new HostEndpointCallLimiter(new HostEndpointToolOptions { ProjectName = "p", MaxConcurrentCalls = 3, MaxConcurrentCallsPerCaller = 2 });

        using var a1 = limiter.TryAcquire("a");
        using var a2 = limiter.TryAcquire("a");
        var a3 = limiter.TryAcquire("a");
        using var b1 = limiter.TryAcquire("b");
        var b2 = limiter.TryAcquire("b");

        a1.Should().NotBeNull();
        a2.Should().NotBeNull();
        a3.Should().BeNull("a has its two calls in flight");
        b1.Should().NotBeNull();
        b2.Should().BeNull("three calls are in flight in total");

        b1!.Dispose();
        using var b3 = limiter.TryAcquire("b");
        b3.Should().NotBeNull("a refused call must not keep a slot of its own");
    }

    [TestCase(0, 4)]
    [TestCase(16, 0)]
    public void AddHostEndpointTools_RejectsALimitBelowOne(int total, int perCaller)
    {
        var services = new ServiceCollection();

        var add = () => services.AddHostEndpointTools(x =>
        {
            x.ProjectName = "p";
            x.MaxConcurrentCalls = total;
            x.MaxConcurrentCallsPerCaller = perCaller;
        });

        add.Should().Throw<InvalidOperationException>().WithMessage("*MaxConcurrentCalls*");
    }

    [Test]
    public void TheDefaults_AreSixteenCallsInTotal_FourPerCaller_AndReadOnlyPostOn()
    {
        var options = new HostEndpointToolOptions();

        options.MaxConcurrentCalls.Should().Be(16);
        options.MaxConcurrentCallsPerCaller.Should().Be(4);
        options.AllowReadOnlyPost.Should().BeTrue();
    }

    private static Task<HostEndpointTestHost> StartHeldAsync(HeldEndpoint gate, Action<HostEndpointToolOptions> configure) =>
        HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: configure,
            map: x => x.MapGet("/guarded/held", async () =>
                {
                    gate.Started.TrySetResult();
                    Interlocked.Increment(ref gate.Runs);

                    // Ignores the request's cancellation on purpose, as an endpoint that does not flow its token would.
                    await gate.Release.Task;

                    return Results.Ok(new { done = true });
                })
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("guarded_held", "Waits for the test.", readOnly: true));

    private static async Task<ModelContextProtocol.Protocol.CallToolResult> RetryWhileBusyAsync(Func<Task<ModelContextProtocol.Protocol.CallToolResult>> call)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            var result = await call();
            if (!Text(result).Contains("Too many host endpoint calls") || DateTime.UtcNow > deadline)
            {
                return result;
            }

            await Task.Delay(20);
        }
    }

    private sealed class HeldEndpoint
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Runs;
    }
}
