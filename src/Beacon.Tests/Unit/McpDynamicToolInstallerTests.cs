using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;
using Beacon.Core.Mcp;
using Beacon.Core.SavedQueries;
using Beacon.MCP.DynamicTools;
using Beacon.MCP.HostEndpoints;
using Beacon.MCP.SavedQueries;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostEndpoints;

namespace Beacon.Tests.Unit;

/// <summary>
/// The installer behind the saved-query and host endpoint tools: each source chained where it registers, call routing
/// with fall-through to the handler installed before, a tool list that fails rather than shortens, idempotent
/// registration, a startup failure for a name two sources claim, and a warning (not a failure) for an attribute tool
/// with a name a source claims.
/// </summary>
[TestFixture]
public class McpDynamicToolInstallerTests
{
    private const string PreviousToolName = "previous_tool";
    private const string AttributeAnswer = "attribute tool answered";

    [Test]
    public async Task ListTools_ListsThePreviousHandlersTools_ThenEachSourceInRegistrationOrder()
    {
        using var provider = Provider(new FakeScript());

        var result = await ListAsync(provider);

        result.Tools.Select(x => x.Name).Should().Equal(PreviousToolName, "alpha_one", "beta_one");
    }

    [Test]
    public async Task CallTool_GoesToTheSourceThatServesTheName_ElseToThePreviousHandler()
    {
        var script = new FakeScript();
        using var provider = Provider(script);

        var alpha = await CallAsync(provider, "alpha_one");
        var beta = await CallAsync(provider, "beta_one");
        var previous = await CallAsync(provider, PreviousToolName);

        Text(alpha).Should().Be("alpha_ answered alpha_one");
        Text(beta).Should().Be("beta_ answered beta_one");
        Text(previous).Should().Be("previous answered");
        script.Calls.Should().Equal("alpha_one", "beta_one");
    }

    [Test]
    public async Task CallTool_ForANameNothingServes_FailsAsUnknownTool()
    {
        using var provider = Provider(new FakeScript(), withPreviousHandler: false);

        var act = async () => await CallAsync(provider, "nobody_tool");

        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Contain("nobody_tool");
    }

    [Test]
    public async Task ListTools_Fails_WhenASourceMarksTheProjectAccessUndetermined()
    {
        var script = new FakeScript();
        script.Lists["alpha_"] = x =>
        {
            x.GetRequiredService<McpProjectContext>().AccessUndetermined = true;

            return [];
        };
        using var provider = Provider(script);

        var act = async () => await ListAsync(provider);

        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Be(ProjectContextFactory.AccessUndeterminedMessage);
        script.Listed.Should().Equal(["alpha_"], "the list fails at once rather than going on to a shorter answer");
    }

    [Test]
    public async Task ListTools_Fails_WhenASourceThrows()
    {
        var script = new FakeScript();
        script.Lists["alpha_"] = _ => throw new InvalidOperationException("source unavailable");
        using var provider = Provider(script);

        var act = async () => await ListAsync(provider);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("source unavailable");
    }

    [Test]
    public async Task AddingASourceTwice_InstallsItsHandlersAndTheNameCheckOnce()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(new FakeScript());
        services.AddScoped<McpProjectContext>();
        services.AddMcpDynamicToolSource<AlphaSource>();
        services.AddMcpDynamicToolSource<AlphaSource>();
        services.AddMcpDynamicToolSource<BetaSource>();

        services.Count(x => x.ServiceType == typeof(IMcpDynamicToolSource)).Should().Be(2);
        services.Count(x => x.ImplementationType == typeof(McpDynamicToolHandlers<AlphaSource>)).Should().Be(1);
        services.Count(x => x.ImplementationType == typeof(McpDynamicToolHandlers<BetaSource>)).Should().Be(1);
        services.Count(x => x.ImplementationType == typeof(McpDynamicToolNameCheck)).Should().Be(1);

        using var provider = services.BuildServiceProvider();
        var result = await ListAsync(provider);

        result.Tools.Select(x => x.Name).Should().Equal("alpha_one", "beta_one");
    }

    [Test]
    public void SavedQueryAndHostEndpointNames_DoNotClash()
    {
        var hostEndpoints = new McpDynamicToolNames("the host endpoint tools", ["api_thing_by_id"], []);

        var act = () => McpDynamicToolInstaller.EnsureNoNameClash([SavedQueryClaims(sourceRegistered: true), hostEndpoints]);

        act.Should().NotThrow();
    }

    [TestCase("q_report")]
    [TestCase("search_saved_queries")]
    public void ANameAnotherSourceClaims_FailsWithThatName(string claimedName)
    {
        var other = new McpDynamicToolNames("a host source", [claimedName], ["host_"]);

        var act = () => McpDynamicToolInstaller.EnsureNoNameClash([SavedQueryClaims(sourceRegistered: true), other]);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{claimedName}'*the saved-query tools*a host source*");
    }

    [Test]
    public void OverlappingPrefixes_FailWithTheLongerPrefix()
    {
        var first = new McpDynamicToolNames("first", [], ["api_"]);
        var second = new McpDynamicToolNames("second", [], ["api_v2_"]);

        var act = () => McpDynamicToolInstaller.EnsureNoNameClash([first, second]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'api_v2_*'*first*second*");
    }

    [Test]
    public void SavedQueryTools_ClaimNothing_WithoutCoresSavedQuerySource()
    {
        var claims = SavedQueryClaims(sourceRegistered: false);

        claims.Names.Should().BeEmpty();
        claims.Prefixes.Should().BeEmpty();
        McpDynamicToolInstaller.AttributeToolsClaimedBySources([claims], ["q_custom", "search_saved_queries"]).Should().BeEmpty();
    }

    [Test]
    public void AnAttributeToolWithAClaimedName_IsReportedWithItsSource()
    {
        var shadowed = McpDynamicToolInstaller.AttributeToolsClaimedBySources([SavedQueryClaims(sourceRegistered: true)], ["get_context", "q_custom"]);

        shadowed.Should().Equal([("q_custom", "the saved-query tools")]);
    }

    [Test]
    public async Task Startup_Fails_WhenASourceClaimsAnEndpointToolName()
    {
        var act = async () =>
        {
            await using var host = await HostEndpointTestHost.StartAsync(
                withMcpServer: true,
                services: x =>
                {
                    x.AddSingleton(new FakeScript());
                    x.AddMcpDynamicToolSource<ApiNamedSource>();
                });
        };

        (await act.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("'api_thing_by_id'");
    }

    [Test]
    public async Task Startup_Succeeds_WithTheSavedQueryAndEndpointToolsTogether()
    {
        await using var host = await HostEndpointTestHost.StartAsync(withMcpServer: true);

        host.App.Services.GetServices<IMcpDynamicToolSource>().Select(x => x.GetType())
            .Should().BeEquivalentTo([typeof(SavedQueryDynamicToolSource), typeof(HostEndpointDynamicToolSource)]);
        host.App.Services.GetServices<IStartupFilter>().OfType<McpDynamicToolNameCheck>().Should().ContainSingle();
    }

    [Test]
    public async Task AnAttributeTool_InTheSavedQueryPrefix_WithNoSavedQueries_StartsWarnsAndAnswers()
    {
        var logs = new LogRecorder();
        await using var host = await StartWithAttributeToolAsync("q_custom", logs, savedQueries: true);
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("q_custom", new Dictionary<string, object?>());

        HostEndpointDispatchTests.Text(result).Should().Be(AttributeAnswer);
        logs.Entries.Should().ContainSingle(x => x.Level == LogLevel.Warning && x.Message.Contains("q_custom") && x.Message.Contains("the saved-query tools"));
    }

    [Test]
    public async Task AnAttributeTool_InTheEndpointPrefix_WithNoEndpointTools_StartsQuietlyAndAnswers()
    {
        var logs = new LogRecorder();
        await using var host = await StartWithAttributeToolAsync("api_custom", logs, controllers: [], mapDefaultMinimalApis: false);
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("api_custom", new Dictionary<string, object?>());

        host.Registry.Tools.Should().BeEmpty();
        HostEndpointDispatchTests.Text(result).Should().Be(AttributeAnswer);
        logs.Entries.Should().NotContain(x => x.Level == LogLevel.Warning && x.Category == typeof(McpDynamicToolNameCheck).FullName);
    }

    [Test]
    public async Task AnAttributeTool_InTheEndpointPrefix_InCatalogMode_IsAnsweredByTheAttributeTool()
    {
        var logs = new LogRecorder();
        await using var host = await StartWithAttributeToolAsync("api_custom", logs, configure: x => x.NamedToolLimit = 1);
        await using var client = await ConnectAsync(host);

        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("api_custom", new Dictionary<string, object?>());

        host.Registry.IsCatalogMode.Should().BeTrue();
        tools.Select(x => x.Name).Should().Contain(["search_api", "call_api", "api_custom"]);
        HostEndpointDispatchTests.Text(result).Should().Be(AttributeAnswer);
        logs.Entries.Should().NotContain(x => x.Level == LogLevel.Warning && x.Category == typeof(McpDynamicToolNameCheck).FullName);
    }

    private static Task<HostEndpointTestHost> StartWithAttributeToolAsync(
        string toolName,
        LogRecorder logs,
        bool savedQueries = false,
        Type[]? controllers = null,
        bool mapDefaultMinimalApis = true,
        Action<HostEndpointToolOptions>? configure = null)
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));

        return HostEndpointTestHost.StartAsync(
            controllers,
            configure: configure,
            mapDefaultMinimalApis: mapDefaultMinimalApis,
            withMcpServer: true,
            services: x =>
            {
                x.AddSingleton<ILoggerProvider>(logs);
                x.AddSingleton(McpServerTool.Create(() => AttributeAnswer, new McpServerToolCreateOptions { Name = toolName }));
                if (savedQueries)
                {
                    var source = new Mock<ISavedQueryToolSource>();
                    source
                        .Setup(y => y.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync([]);
                    x.AddSingleton(source.Object);
                    x.AddSingleton(Mock.Of<ISavedQueryToolExecutor>());
                }
            },
            beforeMcp: x => x.Use(async (context, next) =>
            {
                context.User = HostEndpointTestHost.McpPrincipal(caller);
                context.Items[typeof(McpCaller)] = caller;
                await next(context);
            }));
    }

    private static async Task<McpClient> ConnectAsync(HostEndpointTestHost host)
    {
        var httpClient = host.App.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, McpDiscoveryPaths.McpPath),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
    }

    private static McpDynamicToolNames SavedQueryClaims(bool sourceRegistered)
    {
        var services = new ServiceCollection();
        if (sourceRegistered)
        {
            services.AddSingleton(Mock.Of<ISavedQueryToolSource>());
        }

        using var provider = services.BuildServiceProvider();

        return new SavedQueryDynamicToolSource().ClaimedNames(provider);
    }

    private static ServiceProvider Provider(FakeScript script, bool withPreviousHandler = true)
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(script);
        services.AddScoped<McpProjectContext>();
        if (withPreviousHandler)
        {
            services.Configure<McpServerOptions>(x =>
            {
                x.Handlers.ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult { Tools = [new Tool { Name = PreviousToolName }] });
                x.Handlers.CallToolHandler = (_, _) => ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "previous answered" }] });
            });
        }

        services.AddMcpDynamicToolSource<AlphaSource>();
        services.AddMcpDynamicToolSource<BetaSource>();

        return services.BuildServiceProvider();
    }

    private static async Task<ListToolsResult> ListAsync(ServiceProvider provider)
    {
        var handler = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Handlers.ListToolsHandler!;
        using var scope = provider.CreateScope();
        var request = new RequestContext<ListToolsRequestParams>(Mock.Of<McpServer>(), new JsonRpcRequest { Method = "tools/list" }, new ListToolsRequestParams())
        {
            Services = scope.ServiceProvider
        };

        return await handler(request, CancellationToken.None);
    }

    private static async Task<CallToolResult> CallAsync(ServiceProvider provider, string name)
    {
        var handler = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.Handlers.CallToolHandler!;
        using var scope = provider.CreateScope();
        var request = new RequestContext<CallToolRequestParams>(Mock.Of<McpServer>(), new JsonRpcRequest { Method = "tools/call" }, new CallToolRequestParams { Name = name })
        {
            Services = scope.ServiceProvider
        };

        return await handler(request, CancellationToken.None);
    }

    private static string Text(CallToolResult result) =>
        result.Content.OfType<TextContentBlock>().Single().Text;

    internal sealed class FakeScript
    {
        public Dictionary<string, Func<IServiceProvider, IReadOnlyList<Tool>>> Lists { get; } = [];

        public List<string> Listed { get; } = [];

        public List<string> Calls { get; } = [];
    }

    internal abstract class FakeSource(FakeScript script, string prefix) : IMcpDynamicToolSource
    {
        public virtual McpDynamicToolNames ClaimedNames(IServiceProvider services) => new(prefix + " source", [], [prefix]);

        public bool Handles(IServiceProvider requestServices, string toolName) =>
            toolName.StartsWith(prefix, StringComparison.Ordinal);

        public Task<IReadOnlyList<Tool>> ListToolsAsync(IServiceProvider requestServices, CancellationToken cancellationToken)
        {
            script.Listed.Add(prefix);
            IReadOnlyList<Tool> tools = script.Lists.TryGetValue(prefix, out var list)
                ? list(requestServices)
                : [new Tool { Name = prefix + "one" }];

            return Task.FromResult(tools);
        }

        public Task<CallToolResult> CallAsync(IServiceProvider requestServices, string toolName, IDictionary<string, JsonElement>? arguments, CancellationToken cancellationToken)
        {
            script.Calls.Add(toolName);

            return Task.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = $"{prefix} answered {toolName}" }] });
        }
    }

    internal sealed class AlphaSource(FakeScript script) : FakeSource(script, "alpha_");

    internal sealed class BetaSource(FakeScript script) : FakeSource(script, "beta_");

    internal sealed class ApiNamedSource(FakeScript script) : FakeSource(script, "custom_")
    {
        public override McpDynamicToolNames ClaimedNames(IServiceProvider services) => new("a custom source", ["api_thing_by_id"], ["custom_"]);
    }
}
