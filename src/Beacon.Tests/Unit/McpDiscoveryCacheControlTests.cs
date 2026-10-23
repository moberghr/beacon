using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;
using Beacon.MCP.Discovery;

namespace Beacon.Tests.Unit;

[TestFixture]
public class McpDiscoveryCacheControlTests
{
    [Test]
    public void BaseUrlDerivedFromRequestHost_IsNotCached()
    {
        var configuration = new ConfigurationBuilder().Build();

        McpDiscoveryEndpoints.CacheControlFor(configuration).Should().Be("no-store");
    }

    [Test]
    public void BlankConfiguredBaseUrl_IsNotCached()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:PublicBaseUrl"] = "  " })
            .Build();

        McpDiscoveryEndpoints.CacheControlFor(configuration).Should().Be("no-store");
    }

    [Test]
    public void ConfiguredBaseUrl_KeepsPublicCaching()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Beacon:PublicBaseUrl"] = "https://beacon.example.com" })
            .Build();

        McpDiscoveryEndpoints.CacheControlFor(configuration).Should().Be("public, max-age=3600");
    }
}
