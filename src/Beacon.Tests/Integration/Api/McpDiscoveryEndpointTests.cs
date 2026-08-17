using System.Net;
using System.Text.Json.Nodes;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Integration.Api;

/// <summary>
/// Tier-3 discovery surface (spec 2026-08-17, batch D1): the RFC 9728 protected-resource
/// metadata and the SEP-2127 (draft) server card are served anonymously at /.well-known/*, and
/// every 401 from /beacon/mcp carries the WWW-Authenticate resource_metadata pointer that remote
/// clients bootstrap OAuth discovery from.
/// </summary>
[TestFixture]
[Category("Phase1Harness")]
public class McpDiscoveryEndpointTests
{
    private BeaconWebApplicationFactory? _factory;
    private HttpClient? _client;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        try
        {
            _factory = new BeaconWebApplicationFactory();
            _client = _factory.CreateClient();
        }
        catch (Exception ex)
        {
            Assert.Inconclusive(
                $"Beacon host failed to bootstrap: {ex.Message}. " +
                $"Set {BeaconWebApplicationFactory.TestConnectionStringEnvVar} to a reachable Postgres connection string.");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [TestCase("/.well-known/oauth-protected-resource")]
    [TestCase("/.well-known/oauth-protected-resource/beacon/mcp")]
    public async Task ProtectedResourceMetadata_Anonymous_ReturnsRfc9728Document(string path)
    {
        var response = await _client!.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "discovery metadata is anonymous by design (RFC 9728)");
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        var document = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        document["resource"]!.GetValue<string>().Should().EndWith("/beacon/mcp");
        document["scopes_supported"]!.AsArray()
            .Select(x => x!.GetValue<string>())
            .Should().Contain(["Execute", "Admin"]);
        document.ContainsKey("authorization_servers").Should().BeFalse(
            "the test harness runs with OIDC disabled, so no authorization server may be advertised");
    }

    [Test]
    public async Task ServerCard_Anonymous_ListsAllEightTools()
    {
        var response = await _client!.GetAsync("/.well-known/mcp/server-card.json");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the server card is anonymous by design (SEP-2127 draft)");

        var card = JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        card["transport"]!.GetValue<string>().Should().Be("streamable-http");
        card["endpoint"]!.GetValue<string>().Should().EndWith("/beacon/mcp");

        var toolNames = card["tools"]!.AsArray()
            .Select(x => x!["name"]!.GetValue<string>())
            .ToList();

        toolNames.Should().BeEquivalentTo(
            ["get_context", "ask", "query", "get_documentation", "search", "feedback", "dry_run", "get_query_context"],
            "the card must list all 8 MCP tools");
    }

    [Test]
    public async Task AnonymousMcpRequest_Returns401WithResourceMetadataChallenge()
    {
        var response = await _client!.GetAsync("/beacon/mcp");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should()
            .Contain("resource_metadata", "RFC 9728 §5.1 — the challenge carries the metadata pointer")
            .And.Contain("/.well-known/oauth-protected-resource");
    }
}
