using System.Diagnostics;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core.HostEndpoints;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// SC14 — <c>HostEndpointDispatcher</c> sets the synthetic request's <c>TraceIdentifier</c> to the current
/// activity's W3C id when one is current, and falls back to a synthetic <c>beacon-mcp-</c> id otherwise, so host
/// code that logs <c>TraceIdentifier</c> joins the MCP trace. Exercised through the real
/// <c>DispatchAsync</c> path (not a direct call into a leaf helper): the dispatcher runs the endpoint inside an
/// <c>ExecutionContext.SuppressFlow()</c>'d <c>Task.Run</c>, which — because <c>Activity.Current</c> is itself
/// AsyncLocal-backed — would silently read back <c>null</c> there if the trace id weren't captured on the
/// still-flowing caller side and threaded through explicitly. A test that only calls the id-assignment helper
/// directly would not catch a regression that moves the <c>Activity.Current</c> read to the wrong side of that
/// boundary, so this test goes through the dispatcher's public entry point with a recording
/// <see cref="IHostEndpointDispatchMiddleware"/> instead.
/// </summary>
[TestFixture]
[NonParallelizable]
public class HostEndpointDispatcherTraceTests
{
    // Const, never a static ActivitySource field: referencing that inside ShouldListenTo re-enters its static ctor.
    private const string SourceName = "Beacon.Tests.HostEndpointDispatcherTrace";

    private static readonly ActivitySource Source = new(SourceName);

    private ActivityListener _activityListener = null!;

    [SetUp]
    public void SetUp()
    {
        _activityListener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    [TearDown]
    public void TearDown()
    {
        _activityListener.Dispose();
        Activity.Current = null;
    }

    [Test]
    public async Task WithACurrentActivity_TheDispatchedRequestsTraceIdentifier_IsTheActivityId()
    {
        var captured = new List<string>();
        await using var host = await HostEndpointTestHost.StartAsync(
            services: x => x.AddSingleton<IHostEndpointDispatchMiddleware>(new TraceCapturingMiddleware(captured)));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(
            HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings))));

        using var activity = Source.StartActivity("tools/call");
        activity.Should().NotBeNull("the listener above samples every activity from this source");

        await service.CallAsync("api_minimal_echo", HostEndpointDispatchTests.Args(new { name = "x", count = 1 }), CancellationToken.None);

        captured.Should().ContainSingle().Which.Should().Be(activity!.Id);
    }

    [Test]
    public async Task WithNoCurrentActivity_TheDispatchedRequestsTraceIdentifier_FallsBackToASyntheticId()
    {
        var captured = new List<string>();
        await using var host = await HostEndpointTestHost.StartAsync(
            services: x => x.AddSingleton<IHostEndpointDispatchMiddleware>(new TraceCapturingMiddleware(captured)));
        var service = host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(
            HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings))));
        Activity.Current = null;

        await service.CallAsync("api_minimal_echo", HostEndpointDispatchTests.Args(new { name = "x", count = 1 }), CancellationToken.None);

        captured.Should().ContainSingle().Which.Should().StartWith("beacon-mcp-");
    }

    /// <summary>Records the synthetic request's <c>TraceIdentifier</c> as the dispatcher's middleware chain sees it.</summary>
    private sealed class TraceCapturingMiddleware(List<string> captured) : IHostEndpointDispatchMiddleware
    {
        public Task InvokeAsync(HttpContext syntheticContext, McpCaller caller, Func<Task> next, CancellationToken cancellationToken)
        {
            lock (captured)
            {
                captured.Add(syntheticContext.TraceIdentifier);
            }

            return next();
        }
    }
}
