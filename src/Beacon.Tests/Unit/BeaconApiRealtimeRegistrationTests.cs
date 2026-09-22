using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Api.Hubs;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Authorization;
using Beacon.Core.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The library self-wires realtime: <see cref="ServiceConfiguration.AddBeaconApiServices(IServiceCollection)"/>
/// registers SignalR and the hub-backed notifier, and <see cref="BeaconApiEndpoints.MapBeaconApi"/>
/// maps the hub. These tests pin the default, the opt-out, and the load-bearing assumption that a
/// host may still call <c>AddSignalR()</c> itself.
/// </summary>
[TestFixture]
public class BeaconApiRealtimeRegistrationTests
{
    [Test]
    public void Default_RegistersSignalRApprovalNotifier()
    {
        using var provider = BuildProvider(x => x.AddBeaconApiServices());

        var notifier = provider.GetRequiredService<IApprovalNotifier>();
        notifier.Should().BeOfType<SignalRApprovalNotifier>();
        provider.GetService<IHubContext<BeaconHub>>().Should().NotBeNull("the library must register SignalR itself");
    }

    [Test]
    public void RealtimeDisabled_RegistersDisabledNotifier()
    {
        using var provider = BuildProvider(x => x.AddBeaconApiServices(y => y.Realtime = false));

        provider.GetRequiredService<IApprovalNotifier>().Should().BeOfType<RealtimeDisabledApprovalNotifier>();
    }

    [Test]
    public void RealtimeDisabled_DoesNotRegisterSignalR()
    {
        using var provider = BuildProvider(x => x.AddBeaconApiServices(y => y.Realtime = false));

        provider.GetService<IHubContext<BeaconHub>>().Should().BeNull("Realtime = false must not pull SignalR services in");
    }

    [Test]
    public void HostAddSignalRFirst_StillResolves()
    {
        using var provider = BuildProvider(x =>
        {
            x.AddSignalR();
            x.AddBeaconApiServices();
        });

        provider.GetRequiredService<IApprovalNotifier>().Should().BeOfType<SignalRApprovalNotifier>();
        provider.GetService<IHubContext<BeaconHub>>().Should().NotBeNull("AddSignalR() must be safe to call twice");
    }

    [Test]
    public void HostOwnNotifier_IsNotOverridden()
    {
        using var provider = BuildProvider(x =>
        {
            x.AddScoped<IApprovalNotifier, HostApprovalNotifier>();
            x.AddBeaconApiServices();
        });

        provider.GetRequiredService<IApprovalNotifier>().Should().BeOfType<HostApprovalNotifier>();
    }

    [Test]
    public void ConfigureSignalR_IsAppliedWhenRealtimeEnabled()
    {
        var applied = false;

        using var provider = BuildProvider(x => x.AddBeaconApiServices(y =>
        {
            y.ConfigureSignalR = _ => applied = true;
        }));

        applied.Should().BeTrue();
    }

    [Test]
    public async Task MapBeaconApi_MapsHub_WhenRealtimeEnabled()
    {
        var endpoints = await MapAndCollectRootEndpointsAsync(realtime: true);

        var hub = endpoints
            .Where(x => (x.RoutePattern.RawText ?? string.Empty).StartsWith("/beacon/api/hub", StringComparison.OrdinalIgnoreCase))
            .ToList();

        hub.Select(x => x.RoutePattern.RawText).Should().Contain("/beacon/api/hub/negotiate");
        hub.Should().OnlyContain(x => x.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(y => y.Policy == BeaconApiEndpoints.AuthPolicyName));
    }

    [Test]
    public async Task MapBeaconApi_SkipsHub_WhenRealtimeDisabled()
    {
        var endpoints = await MapAndCollectRootEndpointsAsync(realtime: false);

        endpoints.Should().BeEmpty("the hub is the only route MapBeaconApi puts outside the /beacon/api group");
    }

    [Test]
    public void MapBeaconApi_WithoutAddBeaconApiServices_DoesNotThrowAndMapsNoHub()
    {
        // Published versions documented MapBeaconApi() with no AddBeaconApiServices() and no
        // AddSignalR(). Assuming realtime-on here would call MapHub without SignalR registered and
        // crash every such host at startup on a package bump. No options must mean no hub.
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddLogging();
        builder.Services.AddAuthorization();
        var app = builder.Build();

        var act = () => app.MapBeaconApi();

        act.Should().NotThrow("a host on the previously documented recipe must still boot");

        var rootEndpoints = CollectRootEndpoints(app);
        rootEndpoints.Should().BeEmpty("no BeaconApiOptions means realtime is off, so no hub route");
    }

    [Test]
    public void AddBeaconApiServices_RegistersUserContext_WithoutCookieAuthentication()
    {
        // ~28 Core handlers require IBeaconUserContext. It used to be registered only inside
        // AddBeaconCookieAuthentication, so a JWT- or API-key-only host failed DI validation on
        // every one of them. AddBeaconApiServices must supply it on its own.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiServices();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<IBeaconUserContext>()
            .Should().BeOfType<HttpContextUserContext>(
                "AddBeaconApiServices must register the real user context without cookie auth");
        provider.GetService<IHttpContextAccessor>()
            .Should().NotBeNull("HttpContextUserContext depends on IHttpContextAccessor");
    }

    private static ServiceProvider BuildProvider(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        configure(services);

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Returns only the endpoints <c>MapBeaconApi</c> maps on the ROOT builder — i.e. the hub,
    /// which must live outside the <c>/beacon/api</c> group because the group's antiforgery filter
    /// would break the SignalR handshake. The group's own data source is deliberately not
    /// materialised: minimal-API parameter inference would need the host's full service graph,
    /// which this unit test has no business standing up.
    /// </summary>
    private static async Task<List<RouteEndpoint>> MapAndCollectRootEndpointsAsync(bool realtime)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddBeaconApiServices(y => y.Realtime = realtime);

        await using var app = builder.Build();
        app.MapBeaconApi();

        ((IEndpointRouteBuilder)app).DataSources
            .Should().Contain(x => x.GetType().Name.Contains("Group", StringComparison.Ordinal),
                "the /beacon/api group must be mapped regardless of the realtime setting");

        return CollectRootEndpoints(app);
    }

    /// <summary>
    /// Only the data sources <c>MapBeaconApi</c> adds on the ROOT builder — the group's own source
    /// cannot be materialised in a unit test (minimal-API parameter inference needs the host's full
    /// service graph). The hub must live on the root anyway: the group's antiforgery filter would
    /// reject the negotiate handshake.
    /// </summary>
    private static List<RouteEndpoint> CollectRootEndpoints(WebApplication app)
    {
        return ((IEndpointRouteBuilder)app).DataSources
            .Where(x => !x.GetType().Name.Contains("Group", StringComparison.Ordinal))
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .ToList();
    }

    private sealed class HostApprovalNotifier : IApprovalNotifier
    {
        public Task ApprovalUpdatedAsync(
            int approvalId,
            string status,
            string? reviewerUserId,
            string? requesterUserId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

}
