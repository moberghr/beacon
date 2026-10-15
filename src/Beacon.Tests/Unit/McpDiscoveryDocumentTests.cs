using System.Reflection;
using System.Text.Json.Nodes;
using FluentAssertions;
using Moq;
using ModelContextProtocol.Server;
using NUnit.Framework;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;
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
            .Should().Equal("Execute");
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
    public void ResolveAuthorizationServers_NothingConfigured_KeepsTheLegacyOidcAuthority()
    {
        var options = new McpProtectedResourceOptions();

        McpDiscoveryDocuments.ResolveAuthorizationServers(options, EntraJwt(), "https://beacon-sso.example")
            .Should().Equal(["https://beacon-sso.example"], "without the new section the document is unchanged");
        McpDiscoveryDocuments.ResolveAuthorizationServers(options, EntraJwt(), null)
            .Should().BeEmpty("a JWT issuer is never published unless the host opts in");
        McpDiscoveryDocuments.ResolveScopesSupported(options).Should().Equal("Execute");
    }

    [Test]
    public void ResolveAuthorizationServers_ExplicitList_WinsOverDerivationAndOidc()
    {
        var options = new McpProtectedResourceOptions
        {
            AuthorizationServers = [" https://login.microsoftonline.com/explicit/v2.0 ", ""],
            DeriveAuthorizationServerFromJwtIssuer = true
        };

        McpDiscoveryDocuments.ResolveAuthorizationServers(options, EntraJwt(), "https://beacon-sso.example")
            .Should().Equal("https://login.microsoftonline.com/explicit/v2.0");
    }

    [Test]
    public void ResolveAuthorizationServers_Derived_PrefersTheEntraV2IssuerVerbatim()
    {
        var options = new McpProtectedResourceOptions { DeriveAuthorizationServerFromJwtIssuer = true };

        McpDiscoveryDocuments.ResolveAuthorizationServers(options, EntraJwt(), "https://beacon-sso.example")
            .Should().Equal("https://login.microsoftonline.com/tenant-1/v2.0");

        var v1Only = EntraJwt();
        v1Only.Validation.ValidIssuers = ["https://login.microsoftonline.com/{tenantid}/v2.0", "https://sts.windows.net/tenant-1/"];
        McpDiscoveryDocuments.ResolveAuthorizationServers(options, v1Only, null)
            .Should().Equal(["https://sts.windows.net/tenant-1/"], "templated issuers are skipped and the issuer is not trimmed");
    }

    [Test]
    public void ResolveAuthorizationServers_DerivedWithoutBearerJwt_PublishesNone()
    {
        var options = new McpProtectedResourceOptions { DeriveAuthorizationServerFromJwtIssuer = true };
        var disabled = EntraJwt();
        disabled.EnableBearerAuthentication = false;

        McpDiscoveryDocuments.ResolveAuthorizationServers(options, null, null).Should().BeEmpty();
        McpDiscoveryDocuments.ResolveAuthorizationServers(options, disabled, null).Should().BeEmpty();
    }

    [Test]
    public void ProtectedResourceMetadata_WithEntraScopes_ReplacesTheBeaconScopeNames()
    {
        var options = new McpProtectedResourceOptions { ScopesSupported = ["api://app-id/mcp.access", "offline_access"] };
        var scopes = McpDiscoveryDocuments.ResolveScopesSupported(options);

        var document = McpDiscoveryDocuments.BuildProtectedResourceMetadata(
            BaseUrl, ["https://login.microsoftonline.com/tenant-1/v2.0"], scopes);
        var card = McpDiscoveryDocuments.BuildServerCard(BaseUrl, scopes);

        document["scopes_supported"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().Equal("api://app-id/mcp.access", "offline_access");
        document["authorization_servers"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().Equal("https://login.microsoftonline.com/tenant-1/v2.0");
        card["authentication"]!["scopes_supported"]!.AsArray().Select(x => x!.GetValue<string>())
            .Should().Equal("api://app-id/mcp.access", "offline_access");
        card["endpoint"]!.GetValue<string>().Should().Be(document["resource"]!.GetValue<string>());
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
            .Should().Equal("Execute");
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

    private static JwtAuthenticationOptions EntraJwt()
    {
        return new JwtAuthenticationOptions
        {
            EnableBearerAuthentication = true,
            Validation = new JwtValidationOptions
            {
                ValidIssuers = ["https://sts.windows.net/tenant-1/", "https://login.microsoftonline.com/tenant-1/v2.0"]
            }
        };
    }

    [Test]
    public void PlaygroundToolNames_DeriveFromCatalog()
    {
        var playground = new McpPlaygroundService(Mock.Of<IServiceProvider>());

        playground.ToolNames.Should().Equal(McpToolCatalog.Names);
    }
}
