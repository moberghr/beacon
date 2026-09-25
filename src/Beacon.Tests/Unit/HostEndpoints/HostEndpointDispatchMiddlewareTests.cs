using System.Security.Claims;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Core.HostEndpoints;
using static Beacon.Tests.Unit.HostEndpoints.HostEndpointDispatchTests;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// Host middleware does not run on dispatch; <see cref="IHostEndpointDispatchMiddleware"/> is the host's stand-in.
/// Netgiro's [Permission] filter reads Thread.CurrentPrincipal, which only such a middleware can set.
/// </summary>
[TestFixture]
public class HostEndpointDispatchMiddlewareTests
{
    private static readonly Claim AmbientLoans = HostEndpointTestClaims.Permission("loans.ambient");

    [Test]
    public async Task AnAsyncMiddlewareSettingTheAmbientPrincipal_LetsTheHostsFilterAllowTheCall()
    {
        await using var host = await StartAsync(x => x.AddSingleton<IHostEndpointDispatchMiddleware, AmbientPrincipalMiddleware>());

        var result = await Service(host).CallAsync("api_ambient_loans", null, CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("user").GetString().Should().Be("routine-runner");
    }

    [Test]
    public async Task WithoutTheMiddleware_TheAmbientFilterDenies()
    {
        await using var host = await StartAsync();

        var result = await Service(host).CallAsync("api_ambient_loans", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().StartWith("The host endpoint returned HTTP 403 Forbidden.");
    }

    [Test]
    public async Task Middlewares_RunInRegistrationOrder_AroundTheEndpoint()
    {
        var order = new List<string>();
        await using var host = await StartAsync(x =>
        {
            x.AddSingleton<IHostEndpointDispatchMiddleware>(new OrderRecordingMiddleware("first", order));
            x.AddSingleton<IHostEndpointDispatchMiddleware>(new OrderRecordingMiddleware("second", order));
            x.AddSingleton<IHostEndpointDispatchMiddleware, AmbientPrincipalMiddleware>();
        });

        var result = await Service(host).CallAsync("api_ambient_loans", null, CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        order.Should().Equal("first", "second");
    }

    [Test]
    public async Task TheDispatch_DoesNotChangeTheMcpFlowsAmbientPrincipal()
    {
        await using var host = await StartAsync(x => x.AddSingleton<IHostEndpointDispatchMiddleware, AmbientPrincipalMiddleware>());
        var outer = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mcp-flow")], "Outer"));
        Thread.CurrentPrincipal = outer;

        await Service(host).CallAsync("api_ambient_loans", null, CancellationToken.None);

        Thread.CurrentPrincipal.Should().BeSameAs(outer);
    }

    [Test]
    public async Task AMiddlewareThatDoesNotCallNext_RefusesTheCall_AndIsAudited()
    {
        await using var host = await StartAsync(x => x.AddSingleton<IHostEndpointDispatchMiddleware, ShortCircuitMiddleware>());

        var result = await Service(host).CallAsync("api_ambient_loans", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("Refused by the host application.");
        host.AuditLogs.Should().ContainSingle().Which.ErrorMessage.Should().StartWith("Refused by the host application");
    }

    [Test]
    public async Task AFailingMiddleware_IsAGenericError_AndAudited()
    {
        await using var host = await StartAsync(x => x.AddSingleton<IHostEndpointDispatchMiddleware, ThrowingMiddleware>());

        var result = await Service(host).CallAsync("api_ambient_loans", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().NotContain("must not leak");
        host.AuditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be("Execution failed with an unexpected error.");
    }

    private static Beacon.MCP.HostEndpoints.HostEndpointToolService Service(HostEndpointTestHost host) =>
        host.CreateService(HostEndpointTestHost.OuterRequest(HostEndpointTestHost.SystemCaller(AmbientLoans)));

    private static Task<HostEndpointTestHost> StartAsync(Action<IServiceCollection>? services = null) =>
        HostEndpointTestHost.StartAsync([typeof(AmbientController)], mapDefaultMinimalApis: false, services: services);
}
