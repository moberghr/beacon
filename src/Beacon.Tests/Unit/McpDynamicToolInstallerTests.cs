using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;
using Beacon.MCP.DynamicTools;
using Beacon.MCP.SavedQueries;
using Beacon.MCP.Services;
using Beacon.Tests.Unit.HostEndpoints;

namespace Beacon.Tests.Unit;

/// <summary>
/// The one installer behind the saved-query and host endpoint tools: source order in <c>tools/list</c>, call routing
/// with fall-through to the handler installed before, a tool list that fails rather than shortens, idempotent
/// registration, and the startup check that refuses a tool name claimed twice.
/// </summary>
[TestFixture]
public class McpDynamicToolInstallerTests
{
    private const string PreviousToolName = "previous_tool";

    [Test]
    public async Task ListTools_ListsThePreviousHandlersTools_ThenEachSourceInOrder()
    {
        using var provider = Provider(new FakeScript());

        var result = await ListAsync(provider);

        result.Tools.Select(x => x.Name).Should().Equal(PreviousToolName, "beta_one", "alpha_one");
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
        script.Lists["beta_"] = x =>
        {
            x.GetRequiredService<McpProjectContext>().AccessUndetermined = true;

            return [];
        };
        using var provider = Provider(script);

        var act = async () => await ListAsync(provider);

        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Be(ProjectContextFactory.AccessUndeterminedMessage);
        script.Listed.Should().Equal(["beta_"], "the list fails at once rather than going on to a shorter answer");
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
    public async Task AddingASourceTwice_InstallsTheHandlersAndTheNameCheckOnce()
    {
        var services = new ServiceCollection();
        services.AddOptions();
        services.AddSingleton(new FakeScript());
        services.AddScoped<McpProjectContext>();
        services.AddMcpDynamicToolSource<AlphaSource>();
        services.AddMcpDynamicToolSource<AlphaSource>();
        services.AddMcpDynamicToolSource<BetaSource>();

        services.Count(x => x.ServiceType == typeof(IMcpDynamicToolSource)).Should().Be(2);
        services.Count(x => x.ImplementationType == typeof(McpDynamicToolHandlers)).Should().Be(1);
        services.Count(x => x.ImplementationType == typeof(McpDynamicToolNameCheck)).Should().Be(1);

        using var provider = services.BuildServiceProvider();
        var result = await ListAsync(provider);

        result.Tools.Select(x => x.Name).Should().Equal("beta_one", "alpha_one");
    }

    [Test]
    public void SavedQueryAndHostEndpointNames_DoNotClash()
    {
        var hostEndpoints = new McpDynamicToolNames("the host endpoint tools", ["search_api", "call_api", "api_thing_by_id"], ["api_"]);

        var act = () => McpDynamicToolInstaller.EnsureNoNameClash(
            [new SavedQueryDynamicToolSource().ClaimedNames(), hostEndpoints],
            ["get_context", "ask", "query", "get_documentation", "search", "feedback", "dry_run", "get_query_context"]);

        act.Should().NotThrow();
    }

    [TestCase("q_report")]
    [TestCase("search_saved_queries")]
    public void ANameAnotherSourceClaims_FailsWithThatName(string claimedName)
    {
        var other = new McpDynamicToolNames("a host source", [claimedName], ["host_"]);

        var act = () => McpDynamicToolInstaller.EnsureNoNameClash([new SavedQueryDynamicToolSource().ClaimedNames(), other], []);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*'{claimedName}'*the saved-query tools*a host source*");
    }

    [Test]
    public void OverlappingPrefixes_FailWithTheLongerPrefix()
    {
        var first = new McpDynamicToolNames("first", [], ["api_"]);
        var second = new McpDynamicToolNames("second", [], ["api_v2_"]);

        var act = () => McpDynamicToolInstaller.EnsureNoNameClash([first, second], []);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'api_v2_*'*first*second*");
    }

    [Test]
    public void AnAttributeToolInASourcesNamespace_Fails()
    {
        var act = () => McpDynamicToolInstaller.EnsureNoNameClash([new SavedQueryDynamicToolSource().ClaimedNames()], ["get_context", "q_custom"]);

        act.Should().Throw<InvalidOperationException>().WithMessage("*'q_custom'*attribute tools*saved-query tools*");
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
            .Should().BeEquivalentTo([typeof(SavedQueryDynamicToolSource), typeof(Beacon.MCP.HostEndpoints.HostEndpointDynamicToolSource)]);
        host.App.Services.GetServices<IStartupFilter>().OfType<McpDynamicToolNameCheck>().Should().ContainSingle();
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

    internal abstract class FakeSource(FakeScript script, string prefix, int order) : IMcpDynamicToolSource
    {
        public int Order => order;

        public virtual McpDynamicToolNames ClaimedNames() => new(prefix + " source", [], [prefix]);

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

    internal sealed class AlphaSource(FakeScript script) : FakeSource(script, "alpha_", 2);

    internal sealed class BetaSource(FakeScript script) : FakeSource(script, "beta_", 1);

    internal sealed class ApiNamedSource(FakeScript script) : FakeSource(script, "custom_", 3)
    {
        public override McpDynamicToolNames ClaimedNames() => new("a custom source", ["api_thing_by_id"], ["custom_"]);
    }
}
