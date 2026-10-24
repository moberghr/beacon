using Beacon.AI;
using Beacon.AI.Services.Eval;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Services;
using Beacon.Gateway;
using Beacon.MCP;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Hosting;

/// <summary>
/// The fluent host chain: <c>AddBeacon</c> and the builder steps after it, <c>UseBeaconPipeline</c> and <c>MapBeacon</c>.
/// Its observable request behaviour is pinned by <see cref="SampleHostPipelineTests"/>, which runs on a chain host too.
/// </summary>
[TestFixture]
public class BeaconHostChainTests
{
    [Test]
    public void UseBeaconPipeline_AddsBeaconsMiddlewares_InTheDocumentedOrder()
    {
        using var app = ChainApp(x =>
        {
            x.Authentication.EnableLoginForm = true;
            x.EnableUserManagement();
        });

        app.UseBeaconPipeline();

        // §1.9: the problem responses for /beacon/api first, so every later middleware's exceptions are mapped, the challenge
        // decoration before any authentication, then API key → bearer JWT → UseAuthentication → Beacon.Auth cookie
        // (UseBeaconAuthentication), authorization, the login-form and first-run redirects, antiforgery.
        BeaconHostApplicationBuilderExtensions.PipelineStages(app).Should().Equal(
            "UseBeaconApiExceptionHandler",
            "UseMcpResourceMetadataChallenge",
            "UseBeaconAuthentication",
            "UseAuthorization",
            "LoginFormAuthMiddleware",
            "FirstRunSetupMiddleware",
            "UseAntiforgery");
    }

    [Test]
    public void UseBeaconPipeline_LeavesOutTheRedirects_TheConfigurationTurnsOff()
    {
        using var app = ChainApp();

        app.UseBeaconPipeline();

        BeaconHostApplicationBuilderExtensions.PipelineStages(app).Should().Equal(
            "UseBeaconApiExceptionHandler",
            "UseMcpResourceMetadataChallenge",
            "UseBeaconAuthentication",
            "UseAuthorization",
            "UseAntiforgery");
    }

    [Test]
    public void UseBeaconPipeline_CalledTwice_Throws_NamingTheSecondCall()
    {
        using var app = ChainApp();
        app.UseBeaconPipeline();

        var again = () => app.UseBeaconPipeline();

        again.Should().Throw<InvalidOperationException>()
            .WithMessage("UseBeaconPipeline() has already been called on this application*remove the second call*");
        BeaconHostApplicationBuilderExtensions.PipelineStages(app).Should().HaveCount(5, "the second call added nothing");
    }

    [Test]
    public void MapBeacon_MapsTheApi_SignIn_Mcp_Discovery_AndTheShell()
    {
        using var app = ChainApp();

        app.MapBeacon();

        var routes = Routes(app);
        routes.Should().ContainKey("/beacon/api/health");
        routes.Should().ContainKey("/beacon/api/auth/me");
        routes.Should().ContainKey("/beacon/api/auth/logout");
        routes.Should().ContainKey("/.well-known/oauth-protected-resource");
        routes.Should().ContainKey("/.well-known/mcp/server-card.json");
        routes.Should().ContainKey("/beacon", "the shell's fallback at its mount path");
        routes.Keys.Should().NotContain(x => x.StartsWith("/beacon/api/setup", StringComparison.Ordinal), "user management is off");
        routes.Should().ContainKey("/beacon/mcp");
        routes["/beacon/mcp"]
            .SelectMany(x => x.Metadata.GetOrderedMetadata<IAuthorizeData>())
            .Select(x => x.Policy)
            .Should().Contain(BeaconApiEndpoints.ExecuteScopePolicyName);
    }

    [Test]
    public void MapBeacon_WithUserManagement_MapsTheSetupEndpoints()
    {
        using var app = ChainApp(x => x.EnableUserManagement());

        app.MapBeacon();

        Routes(app).Should().ContainKey("/beacon/api/setup/status");
    }

    [Test]
    public void AddBeacon_RefusesUseAI_PointingAtAddAI()
    {
        var act = () => new ServiceCollection().AddBeacon(Configuration(), x =>
        {
            x.AddBeaconScheduler<NoScheduler>();
            x.UseAI = true;
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*call .AddAI()*");
    }

    [Test]
    public void AddAI_SwitchesAiOn_AndTheRealEvalServiceAndConnectionTesterAreResolved()
    {
        var services = new ServiceCollection();

        var builder = services.AddBeacon(Configuration(), x => x.AddBeaconScheduler<NoScheduler>());
        var before = ImplementationOf<IMcpEvalService>(services);
        builder.AddAI();

        before.Should().NotBe(typeof(McpEvalService), "AI was off until AddAI");
        ImplementationOf<IMcpEvalService>(services).Should().Be(typeof(McpEvalService));
        ImplementationOf<ILlmConnectionTester>(services).Should().Be(typeof(Beacon.AI.Handlers.AdminSettings.LlmConnectionTester));
        services.Where(x => x.ServiceType == typeof(IMcpEvalService) || x.ServiceType == typeof(ILlmConnectionTester))
            .Should().HaveCount(2, "the AI-off stand-ins are gone, not shadowed");
        services.Single(x => x.ServiceType == typeof(BeaconConfiguration)).ImplementationInstance
            .Should().BeOfType<BeaconConfiguration>().Which.UseAI.Should().BeTrue();
    }

    [Test]
    public void WithoutAddAI_TheAiOffStandInStays()
    {
        var services = new ServiceCollection();

        services.AddBeacon(Configuration(), x => x.AddBeaconScheduler<NoScheduler>());

        ImplementationOf<IMcpEvalService>(services)!.Name.Should().Be("AiDisabledMcpEvalService");
    }

    [Test]
    public void AddAI_KeepsAHostsOwnEvalService()
    {
        var services = new ServiceCollection();
        services.AddTransient<IMcpEvalService, HostEvalService>();

        services.AddBeacon(Configuration(), x => x.AddBeaconScheduler<NoScheduler>()).AddAI();

        ImplementationOf<IMcpEvalService>(services).Should().Be(typeof(HostEvalService));
    }

    [Test]
    public void AddAI_CalledTwice_Throws()
    {
        var builder = new ServiceCollection().AddBeacon(Configuration(), x => x.AddBeaconScheduler<NoScheduler>()).AddAI();

        var again = () => builder.AddAI();

        again.Should().Throw<InvalidOperationException>().WithMessage("Beacon's AI services are already registered*");
    }

    [Test]
    public void AddAI_AfterTheOlderAddBeaconAI_Throws()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        var builder = services.AddBeaconServices(configuration, x => x.AddBeaconScheduler<NoScheduler>());
        services.AddBeaconAI(configuration);

        var act = () => builder.AddAI();

        act.Should().Throw<InvalidOperationException>().WithMessage("Beacon's AI services are already registered*not beside AddBeaconAI*");
    }

    [Test]
    public void OlderAddBeaconAI_AfterAddAI_Throws()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        services.AddBeacon(configuration, x => x.AddBeaconScheduler<NoScheduler>()).AddAI();

        var act = () => services.AddBeaconAI(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("This host is wired with the chain*add AI with .AddAI()*");
    }

    [Test]
    public void OlderAddBeaconAI_OnAHostStartedWithAddBeacon_Throws_InsteadOfLeavingTheStandInsInPlace()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        services.AddBeacon(configuration, x => x.AddBeaconScheduler<NoScheduler>());

        var act = () => services.AddBeaconAI(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage("This host is wired with the chain*");
        services.Should().NotContain(x => x.ImplementationType == typeof(McpEvalService), "nothing of AI was registered");
    }

    [Test]
    public void AddMcp_AfterTheOlderAddBeaconMcp_Throws()
    {
        var services = new ServiceCollection();
        var builder = services.AddBeaconServices(Configuration(), x => x.AddBeaconScheduler<NoScheduler>());
        services.AddBeaconMcp();
        builder.AddAI();

        var act = () => builder.AddMcp();

        act.Should().Throw<InvalidOperationException>().WithMessage("Beacon's MCP server is already registered*not beside AddBeaconMcp*");
    }

    [Test]
    public void OlderAddBeaconMcp_AfterAddMcp_Throws()
    {
        var services = new ServiceCollection();
        services.AddBeacon(Configuration(), x => x.AddBeaconScheduler<NoScheduler>()).AddAI().AddMcp();

        var act = () => services.AddBeaconMcp();

        act.Should().Throw<InvalidOperationException>().WithMessage("This host is wired with the chain*with .AddMcp()*");
    }

    [Test]
    public void AddMcp_WithAiFromTheOlderAddBeaconAI_Throws()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();
        var builder = services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoScheduler>();
            x.UseAI = true;
        });
        services.AddBeaconAI(configuration);

        var act = () => builder.AddMcp();

        act.Should().Throw<InvalidOperationException>().WithMessage("AddMcp() needs Beacon's AI services: call .AddAI() before it.");
    }

    [Test]
    public void TheOlderEntryPoints_UsedAlone_StillRegisterAiAndMcp()
    {
        var services = new ServiceCollection();
        var configuration = Configuration();

        services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<NoScheduler>();
            x.UseAI = true;
        });
        services.AddBeaconAI(configuration);
        services.AddBeaconMcp();

        ImplementationOf<IMcpEvalService>(services).Should().Be(typeof(McpEvalService));
        services.Should().ContainSingle(x => x.ServiceType == typeof(BeaconMcpOptions));
    }

    [Test]
    public void AddApi_SetsTheShellsAntiforgeryHeader_AndBeaconsCookieName()
    {
        using var app = ChainApp();

        var antiforgery = app.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value;

        antiforgery.HeaderName.Should().Be("X-XSRF-TOKEN");
        antiforgery.Cookie.Name.Should().Be(".Beacon.Antiforgery");
    }

    [Test]
    public void MapBeacon_WithoutCookieAuth_MapsNoSignInEndpoints()
    {
        using var app = ChainApp(cookieAuth: false);

        app.MapBeacon();

        var routes = Routes(app);
        routes.Should().ContainKey("/beacon/api/health");
        routes.Keys.Should().NotContain(x => x.StartsWith("/beacon/api/auth/log", StringComparison.Ordinal), "sign-in issues the cookie this host does not register");
    }

    [Test]
    public void AddMcp_BeforeAddAI_Throws()
    {
        var builder = new ServiceCollection().AddBeacon(Configuration(), x => x.AddBeaconScheduler<NoScheduler>());

        var act = () => builder.AddMcp();

        act.Should().Throw<InvalidOperationException>().WithMessage("AddMcp() needs Beacon's AI services: call .AddAI() before it.");
    }

    private static WebApplication ChainApp(Action<BeaconConfiguration>? configure = null, bool cookieAuth = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(BeaconHostChain.RequiredSettings());
        BeaconHostChain.Add(builder.Services, builder.Configuration, configure, cookieAuth);

        return builder.Build();
    }

    private static Dictionary<string, List<RouteEndpoint>> Routes(WebApplication app)
    {
        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText != null)
            .GroupBy(x => x.RoutePattern.RawText!.TrimEnd('/'))
            .ToDictionary(x => x.Key, x => x.ToList());
    }

    private static Type? ImplementationOf<TService>(IServiceCollection services)
    {
        return services.LastOrDefault(x => x.ServiceType == typeof(TService))?.ImplementationType;
    }

    private static IConfiguration Configuration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(BeaconHostChain.RequiredSettings())
            .Build();
    }

    private sealed class NoScheduler : Beacon.Core.Worker.IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }

    private sealed class HostEvalService : IMcpEvalService
    {
        public Task<int> StartRunAsync(int? projectId, int? userId, CancellationToken ct) => Task.FromResult(0);

        public Task RunAsync(int runId, CancellationToken ct) => Task.CompletedTask;

        public Task<CaseEvaluation> EvaluateCasePassesAsync(
            int dataSourceId,
            int projectId,
            string question,
            string goldSql,
            string? goldResultFingerprint,
            string? extraContext,
            CancellationToken ct) => throw new NotSupportedException();
    }
}
