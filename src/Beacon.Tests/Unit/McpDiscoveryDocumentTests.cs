using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ModelContextProtocol.Server;
using NUnit.Framework;
using Beacon.MCP;
using Beacon.MCP.Discovery;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Tier-3 discovery documents (spec 2026-08-17, batch D1): RFC 9728 protected-resource metadata,
/// the SEP-2127 (draft) server card, and the single-source tool catalog that both the card and
/// the playground derive from — pinned by reflection against the live [McpServerTool] attributes
/// so a new tool cannot be forgotten.
/// </summary>
[TestFixture]
public class McpDiscoveryDocumentTests
{
    private const string BaseUrl = "https://beacon.example.com";

    [Test]
    public void ProtectedResourceMetadata_WithoutOidc_OmitsAuthorizationServers()
    {
        var document = McpDiscoveryDocuments.BuildProtectedResourceMetadata(BaseUrl, null);

        document["resource"]!.GetValue<string>().Should().Be($"{BaseUrl}/beacon/mcp");
        document.ContainsKey("authorization_servers").Should().BeFalse(
            "an API-key-only deployment has no OAuth authorization server, and an empty array would tell RFC 9728 clients none exists");
        document["bearer_methods_supported"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().Equal("header");
        document["scopes_supported"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().Equal("Execute", "Admin");
        document["resource_name"]!.GetValue<string>().Should().Be("Beacon MCP");
        document["resource_documentation"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
    }

    [Test]
    public void ProtectedResourceMetadata_WithOidc_ListsTheAuthority()
    {
        const string authority = "https://login.microsoftonline.com/tenant-id/v2.0";

        var document = McpDiscoveryDocuments.BuildProtectedResourceMetadata(BaseUrl, authority);

        document["authorization_servers"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().Equal(authority);
    }

    [Test]
    public void ServerCard_CarriesIdentityTransportAndAuthSummary()
    {
        var card = McpDiscoveryDocuments.BuildServerCard(BaseUrl);

        card["name"]!.GetValue<string>().Should().Be("Beacon");
        card["version"]!.GetValue<string>().Should().Be(McpDiscoveryDocuments.ServerVersion);
        card["description"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();
        card["transport"]!.GetValue<string>().Should().Be("streamable-http");
        card["endpoint"]!.GetValue<string>().Should().Be($"{BaseUrl}/beacon/mcp");
        card["documentation"]!.GetValue<string>().Should().NotBeNullOrWhiteSpace();

        var authentication = card["authentication"]!.AsObject();
        authentication["type"]!.GetValue<string>().Should().Be("bearer");
        authentication["scopes_supported"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().Equal("Execute", "Admin");
        authentication["resource_metadata"]!.GetValue<string>()
            .Should().Be($"{BaseUrl}/.well-known/oauth-protected-resource");
    }

    [Test]
    public void ServerCard_ListsEveryCatalogTool()
    {
        var card = McpDiscoveryDocuments.BuildServerCard(BaseUrl);

        var cardTools = card["tools"]!.AsArray()
            .Select(x => (Name: x!["name"]!.GetValue<string>(), Title: x["title"]!.GetValue<string>()))
            .ToList();

        cardTools.Should().Equal(McpToolCatalog.Tools.Select(x => (x.Name, x.Title)));
    }

    [Test]
    public void ToolCatalog_MatchesLiveToolAttributes()
    {
        var liveTools = typeof(ServiceConfiguration).Assembly.GetTypes()
            .Where(x => x.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(x => x.GetCustomAttribute<McpServerToolAttribute>())
            .Where(x => x != null)
            .Select(x => (Name: x!.Name!, Title: x.Title!))
            .OrderBy(x => x.Name)
            .ToList();

        var catalogTools = McpToolCatalog.Tools
            .Select(x => (x.Name, x.Title))
            .OrderBy(x => x.Name)
            .ToList();

        liveTools.Should().NotBeEmpty("Beacon.MCP publishes attribute-discovered tools");
        catalogTools.Should().Equal(
            liveTools,
            "McpToolCatalog is the single source for the server card and playground — a new/renamed [McpServerTool] must be reflected there");
    }

    [Test]
    public void PlaygroundToolNames_DeriveFromCatalog()
    {
        var playground = new McpPlaygroundService(Mock.Of<IServiceProvider>());

        playground.ToolNames.Should().Equal(McpToolCatalog.Names);
    }
}
