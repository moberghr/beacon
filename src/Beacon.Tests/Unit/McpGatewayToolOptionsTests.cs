using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Npgsql;
using NUnit.Framework;
using Beacon.Core.Configuration;
using Beacon.MCP;
using Beacon.MCP.Discovery;
using Beacon.MCP.SavedQueries;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// What a host that embeds Beacon only as the MCP gateway for its own endpoints sets without workarounds: which built-in
/// tools exist (and the server card lists), whether saved queries become tools, discovery defaults contributed by code,
/// and a shared PostgreSQL data source whose pool would leak Beacon's search path, refused.
/// </summary>
[TestFixture]
public class McpGatewayToolOptionsTests
{
    [Test]
    public void OnlyTheBuiltInToolsTheHostServes_Exist()
    {
        using var provider = McpServices(x =>
        {
            x.BuiltInTools.Clear();
            x.BuiltInTools.UnionWith(["get_context", "search", "get_documentation"]);
        });

        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        tools.Select(x => x.ProtocolTool.Name).Should().BeEquivalentTo("get_context", "search", "get_documentation");
    }

    [Test]
    public void ByDefault_AllEightBuiltInToolsExist()
    {
        using var provider = McpServices(_ => { });

        var tools = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ToolCollection!;

        tools.Select(x => x.ProtocolTool.Name).Should().BeEquivalentTo(BeaconMcpOptions.AllBuiltInTools);
    }

    [Test]
    public void AnUnknownBuiltInTool_FailsTheRegistration()
    {
        var act = () => McpServices(x => x.BuiltInTools.Add("delete_everything"));

        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown built-in tool(s) delete_everything*");
    }

    [Test]
    public void SavedQueryTools_CanBeTurnedOff()
    {
        var on = new ServiceCollection();
        var off = new ServiceCollection();

        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(on, _ => { });
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(off, x => x.SavedQueryTools = false);

        on.Should().Contain(x => x.ServiceType == typeof(SavedQueryToolService));
        off.Should().NotContain(x => x.ServiceType == typeof(SavedQueryToolService), "without the service the saved-query handlers serve no tool");
    }

    [Test]
    public void TheServerInstructions_CanBeTheHosts()
    {
        using var provider = McpServices(x => x.ServerInstructions = "Inventhor's own tools.");

        provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInstructions.Should().Be("Inventhor's own tools.");
    }

    [TestCase("Leave Desk", "Leave Desk")]
    [TestCase(null, McpDiscoveryDocuments.ServerName)]
    public void TheServerName_IsTheHostsResourceName_WhenSet(string? resourceName, string expected)
    {
        using var provider = McpServices(_ => { }, new Dictionary<string, string?> { ["Beacon:Mcp:ProtectedResource:ResourceName"] = resourceName });

        var serverInfo = provider.GetRequiredService<IOptions<McpServerOptions>>().Value.ServerInfo!;

        serverInfo.Name.Should().Be(expected, "initialize names the server as the discovery documents do");
        serverInfo.Version.Should().Be(McpDiscoveryDocuments.ServerVersion);
    }

    [Test]
    public void TheServerCard_ListsOnlyTheServedBuiltInTools()
    {
        var card = McpDiscoveryDocuments.BuildServerCard("https://host.example.com", ["Execute"], ["search", "get_documentation"]);

        card["tools"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Should().Equal("get_documentation", "search");
    }

    [Test]
    public async Task TheDiscoveryDocuments_TakeCodeContributedDefaults_AndTheServedTools()
    {
        await using var app = await DiscoveryHostAsync(new LogRecorder(), services =>
        {
            services.PostConfigure<McpProtectedResourceOptions>(x => x.ScopesSupported = ["api://inventhor/mcp.access", "offline_access"]);
            var mcpOptions = new BeaconMcpOptions { SavedQueryTools = false, ServerDescription = "Inventhor's own tools." };
            mcpOptions.BuiltInTools.IntersectWith(["search"]);
            services.AddSingleton(mcpOptions);
        });
        var client = app.GetTestClient();

        var metadata = JsonNode.Parse(await client.GetStringAsync("/.well-known/oauth-protected-resource"))!;
        var card = JsonNode.Parse(await client.GetStringAsync("/.well-known/mcp/server-card.json"))!;

        metadata["scopes_supported"]!.AsArray().Select(x => x!.GetValue<string>()).Should().Equal("api://inventhor/mcp.access", "offline_access");
        card["tools"]!.AsArray().Select(x => x!["name"]!.GetValue<string>()).Should().Equal("search");
        card["description"]!.GetValue<string>().Should().Be("Inventhor's own tools.");
    }

    [TestCase(new[] { "api://inventhor/mcp.access" }, true)]
    [TestCase(new[] { "api://inventhor/mcp.access", "offline_access" }, false)]
    [TestCase(new[] { "Execute" }, false)]
    public async Task AnApiScopeWithoutOfflineAccess_IsWarnedAtStartup(string[] scopes, bool warned)
    {
        var logs = new LogRecorder();
        await using var app = await DiscoveryHostAsync(logs, services =>
            services.PostConfigure<McpProtectedResourceOptions>(x => x.ScopesSupported = [.. scopes]));

        logs.Contains("not offline_access").Should().Be(warned);
    }

    [TestCase("Multiplexing=true", "Multiplexing is on")]
    [TestCase("No Reset On Close=true", "No Reset On Close is on")]
    public void ASharedDataSourceWhosePoolWouldLeakTheSearchPath_IsRefused(string setting, string expected)
    {
        using var dataSource = NpgsqlDataSource.Create($"Host=localhost;Database=host;{setting}");

        var act = () => Beacon.Core.PostgreSql.ServiceCollectionExtensions.RequireConnectionResetOnReturn(dataSource);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }

    [Test]
    public void ASharedDataSourceThatResetsItsConnections_IsAccepted()
    {
        using var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=host");

        var act = () => Beacon.Core.PostgreSql.ServiceCollectionExtensions.RequireConnectionResetOnReturn(dataSource);

        act.Should().NotThrow();
    }

    private static ServiceProvider McpServices(Action<BeaconMcpOptions> configure, Dictionary<string, string?>? settings = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (settings != null)
        {
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        }

        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(services, configure);

        return services.BuildServiceProvider();
    }

    private static async Task<WebApplication> DiscoveryHostAsync(LogRecorder logs, Action<IServiceCollection> services)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:PublicBaseUrl"] = "https://host.example.com" });
        builder.Services.AddRouting();
        services(builder.Services);

        var app = builder.Build();
        app.MapMcpDiscovery();
        await app.StartAsync();

        return app;
    }
}
