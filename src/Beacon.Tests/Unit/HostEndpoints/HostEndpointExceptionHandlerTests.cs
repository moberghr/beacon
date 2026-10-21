using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.MCP.HostEndpoints;
using static Beacon.Tests.Unit.HostEndpoints.HostEndpointDispatchTests;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// Host middleware does not run on dispatch, <c>UseExceptionHandler()</c> included. Beacon runs the host's
/// <see cref="IExceptionHandler"/>s around the call in its place, so an endpoint's deliberate refusal reaches the agent as
/// the status and problem a browser would get, and an exception no handler takes stays a generic failure.
/// </summary>
[TestFixture]
public class HostEndpointExceptionHandlerTests
{
    [Test]
    public async Task ARefusalTheHostsHandlerMaps_ReachesTheAgentAsItsStatusAndBody()
    {
        await using var host = await StartAsync();

        var result = await Service(host).CallAsync("api_not_yours", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("The host endpoint returned HTTP 403 Forbidden.").And.Contain("This asset is not yours.");
        host.AuditLogs.Should().ContainSingle().Which.Tool.Should().Be("api_not_yours");
    }

    [Test]
    public async Task AnExceptionNoHandlerTakes_StaysAGenericFailure_WithoutItsMessage()
    {
        await using var host = await StartAsync();

        var result = await Service(host).CallAsync("api_broken", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("The host endpoint behind api_broken failed with an unexpected error.");
        Text(result).Should().NotContain("must not leak");
    }

    [Test]
    public async Task WithTheHandlersOff_ARefusalIsAGenericFailure()
    {
        await using var host = await StartAsync(x => x.UseHostExceptionHandlers = false);

        var result = await Service(host).CallAsync("api_not_yours", null, CancellationToken.None);

        Text(result).Should().Be("The host endpoint behind api_not_yours failed with an unexpected error.");
    }

    private static HostEndpointToolService Service(HostEndpointTestHost host) =>
        host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings))));

    private static Task<HostEndpointTestHost> StartAsync(Action<HostEndpointToolOptions>? configure = null) =>
        HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: configure,
            services: x => x.AddExceptionHandler<NotYoursExceptionHandler>(),
            map: x =>
            {
                x.MapGet("/assets/not-yours", IResult () => throw new NotYoursException())
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("not_yours", "An asset of somebody else's.", readOnly: true);
                x.MapGet("/assets/broken", IResult () => throw new InvalidOperationException("must not leak"))
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("broken", "A failing read.", readOnly: true);
            });

    private sealed class NotYoursException : Exception;

    // The host's own mapping of a deliberate refusal to a 403 problem, as its UseExceptionHandler() runs it for the browser.
    private sealed class NotYoursExceptionHandler : IExceptionHandler
    {
        public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
        {
            if (exception is not NotYoursException)
            {
                return false;
            }

            httpContext.Response.StatusCode = StatusCodes.Status403Forbidden;
            await httpContext.Response.WriteAsJsonAsync(new { title = "This asset is not yours." }, cancellationToken);

            return true;
        }
    }
}
