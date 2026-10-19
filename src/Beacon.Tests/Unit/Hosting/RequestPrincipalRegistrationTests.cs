using System.Security.Claims;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Worker;
using Beacon.MCP.HostEndpoints;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Hosting;

/// <summary>
/// Core reads the request's caller through <see cref="IBeaconRequestPrincipal"/>: its own default serves no request, the
/// Beacon.Api and Beacon.MCP registrations replace only that default with the web host's, in whichever order they run,
/// a principal the host registered itself stays, and a host's explicit <c>UseRequestPrincipal</c> replaces Beacon's own
/// in any order. Serving Beacon's endpoints over the default fails startup.
/// </summary>
[TestFixture]
public class RequestPrincipalRegistrationTests
{
    private const string WebPrincipal = "Beacon.Shared.Http.HttpContextRequestPrincipal";

    [TestCase(false)]
    [TestCase(true)]
    public void TheWebPrincipal_ReadsTheCurrentRequest_AndNothingWithoutOne(bool throughMcp)
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-1")], "Cookies"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } };
        var principal = TestRequestPrincipal.Web(accessor, throughMcp);

        principal.GetType().FullName.Should().Be(WebPrincipal);
        principal.HasRequest.Should().BeTrue();
        principal.User.Should().BeSameAs(user);

        accessor.HttpContext = null;

        principal.HasRequest.Should().BeFalse("no request is in progress");
        principal.User.Should().BeNull();
    }

    [Test]
    public async Task AWebHostsRequest_ActsAsItsInteractiveUser_AndNeverRunsAsAnOwner()
    {
        var world = new ProjectAccessWorld();
        world.User(7, "ext-7");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-7")], "Cookies"));
        var services = new ServiceCollection();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        Core(services);
        services.AddBeaconApiServices();
        services.AddSingleton(world.Factory());
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var actor = await scope.ServiceProvider.GetRequiredService<IBeaconActorAccessor>().GetCurrentAsync(CancellationToken.None);
        var runAs = () => scope.ServiceProvider.GetRequiredService<IRunAsScope>().RunAs("ext-owner");

        actor.Kind.Should().Be(BeaconPrincipalKind.User, "the request's interactive caller, not the system or nobody");
        actor.UserId.Should().Be("ext-7");
        actor.BeaconUserId.Should().Be(7);
        runAs.Should().Throw<InvalidOperationException>().WithMessage("*request*");
    }

    [Test]
    public void TheApiServices_AloneOverCore_LeaveOneWebPrincipal()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconApiServices();

        ShouldReadTheWebRequest(services, typeof(Beacon.Api.ServiceConfiguration));
    }

    [Test]
    public void TheCookieAuthentication_AloneOverCore_LeavesOneWebPrincipal()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconCookieAuthentication();

        ShouldReadTheWebRequest(services, typeof(Beacon.Api.ServiceConfiguration));
    }

    [Test]
    public void TheMcpServices_AloneOverCore_LeaveOneWebPrincipal()
    {
        var services = Core(new ServiceCollection());
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);

        ShouldReadTheWebRequest(services, typeof(Beacon.MCP.ServiceConfiguration));
    }

    [Test]
    public void TheHostEndpointTools_AloneOverCore_LeaveOneWebPrincipal()
    {
        var services = Core(new ServiceCollection());
        services.AddHostEndpointTools(x => x.ProjectName = "Netgiro");

        ShouldReadTheWebRequest(services, typeof(Beacon.MCP.ServiceConfiguration));
    }

    [Test]
    public void BeaconsWebRegistration_ReplacesCoresDefault_WhenCoreIsRegisteredLast()
    {
        var services = new ServiceCollection();
        services.AddBeaconApiServices();
        Core(services);

        ShouldReadTheWebRequest(services, typeof(Beacon.Api.ServiceConfiguration));
    }

    [Test]
    public void BeaconsWebRegistrations_FromBothApiAndMcp_LeaveOnePrincipal_TheFirstWebOne()
    {
        var services = Core(new ServiceCollection());
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);
        services.AddBeaconCookieAuthentication();
        services.AddBeaconApiServices();

        ShouldReadTheWebRequest(services, typeof(Beacon.MCP.ServiceConfiguration));
    }

    [Test]
    public void AHostsUseRequestPrincipal_AfterApiAndMcp_Wins()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconApiServices();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);
        services.UseRequestPrincipal<HostPrincipal>();

        ShouldResolveTheHostPrincipal(services);
    }

    [Test]
    public void TheLastUseRequestPrincipal_Wins_AlsoOverAnEarlierCallForTheSameType()
    {
        var services = Core(new ServiceCollection());
        services.UseRequestPrincipal<HostPrincipal>();
        services.UseRequestPrincipal<OtherHostPrincipal>();
        services.UseRequestPrincipal<HostPrincipal>();

        services.Where(x => x.ServiceType == typeof(IBeaconRequestPrincipal)).Should().HaveCount(2);
        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IBeaconRequestPrincipal>().Should().BeOfType<HostPrincipal>();
    }

    [Test]
    public void AHostsUseRequestPrincipal_BeforeApiAndMcp_Survives()
    {
        var services = Core(new ServiceCollection());
        services.UseRequestPrincipal<HostPrincipal>();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);
        services.AddBeaconApiServices();

        ShouldResolveTheHostPrincipal(services);
    }

    [Test]
    public void AHostsUseRequestPrincipal_BeforeCore_Survives()
    {
        var services = new ServiceCollection();
        services.UseRequestPrincipal<HostPrincipal>();
        Core(services);
        services.AddBeaconApiServices();

        ShouldResolveTheHostPrincipal(services);
    }

    [Test]
    public void AHostsOwnRegistration_BeforeBeacon_Survives()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IBeaconRequestPrincipal, HostPrincipal>();
        Core(services);
        services.AddBeaconApiServices();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);

        ShouldResolveTheHostPrincipal(services);
    }

    [Test]
    public void AHostsAddSingleton_AfterBeaconsRegistrations_Wins_AsTheLastRegistration()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconApiServices();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services);
        services.AddSingleton<IBeaconRequestPrincipal, HostPrincipal>();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBeaconRequestPrincipal>().Should().BeOfType<HostPrincipal>();
    }

    [Test]
    public void WithoutBeaconsWebRegistrations_CoreServesNoRequest()
    {
        using var provider = Core(new ServiceCollection()).BuildServiceProvider();

        provider.GetRequiredService<IBeaconRequestPrincipal>().Should().BeOfType<NoRequestPrincipal>();
    }

    [Test]
    public void TheStartupCheck_RefusesCoresOffRequestDefault()
    {
        var check = new RequestPrincipalStartupCheck(new NoRequestPrincipal());

        var act = () => check.Configure(_ => { });

        act.Should().Throw<InvalidOperationException>().WithMessage("*IBeaconRequestPrincipal*off-request default*");
    }

    [Test]
    public void TheStartupCheck_PassesAPrincipalThatReadsRequests()
    {
        Action<IApplicationBuilder> next = _ => { };

        new RequestPrincipalStartupCheck(TestRequestPrincipal.For()).Configure(next).Should().BeSameAs(next);
    }

    [Test]
    public void TheStartupCheck_PassesAHostBuiltThroughTheApiServices()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconApiServices();

        ShouldPassTheStartupCheck(services);
    }

    [Test]
    public void TheStartupCheck_PassesAHostBuiltThroughTheCookieAuthentication()
    {
        var services = Core(new ServiceCollection());
        services.AddBeaconCookieAuthentication();

        ShouldPassTheStartupCheck(services);
    }

    [Test]
    public async Task AHostMappingTheApi_OverCoresDefault_FailsToStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        Core(builder.Services);
        builder.Services.AddBeaconApiAuthorization();
        await using var app = builder.Build();

        var act = () => app.StartAsync();

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*off-request default*");
    }

    private static void ShouldReadTheWebRequest(IServiceCollection services, Type registeringAssemblyType)
    {
        services.Where(x => x.ServiceType == typeof(IBeaconRequestPrincipal)).Should().ContainSingle();
        using var provider = services.BuildServiceProvider();
        var principal = provider.GetRequiredService<IBeaconRequestPrincipal>();

        principal.GetType().FullName.Should().Be(WebPrincipal);
        principal.GetType().Assembly.Should().BeSameAs(registeringAssemblyType.Assembly, "the first web registration wins");
        principal.HasRequest.Should().BeFalse("no request is in progress");
    }

    private static void ShouldResolveTheHostPrincipal(IServiceCollection services)
    {
        services.Where(x => x.ServiceType == typeof(IBeaconRequestPrincipal)).Should().ContainSingle();
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IBeaconRequestPrincipal>().Should().BeOfType<HostPrincipal>();
    }

    private static void ShouldPassTheStartupCheck(IServiceCollection services)
    {
        using var provider = services.BuildServiceProvider();
        var check = provider
            .GetServices<IStartupFilter>()
            .OfType<RequestPrincipalStartupCheck>()
            .Single();
        Action<IApplicationBuilder> next = _ => { };

        check.Configure(next).Should().BeSameAs(next);
    }

    private static IServiceCollection Core(IServiceCollection services)
    {
        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:EncryptionKey"] = TestEncryptionKey.Value })
            .Build();
        services.AddLogging();
        services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoOpScheduler>());

        return services;
    }

    private sealed class HostPrincipal : IBeaconRequestPrincipal
    {
        public bool HasRequest => true;

        public ClaimsPrincipal? User => null;
    }

    private sealed class OtherHostPrincipal : IBeaconRequestPrincipal
    {
        public bool HasRequest => false;

        public ClaimsPrincipal? User => null;
    }

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
