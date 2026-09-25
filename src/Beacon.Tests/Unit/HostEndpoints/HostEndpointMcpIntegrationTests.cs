using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;
using Beacon.Core.Mcp;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// End to end through the real MCP SDK: a client lists and calls the endpoint tools over Streamable HTTP, proving
/// the list/call handlers compose with the attribute tools and that the caller on the MCP request reaches dispatch.
/// </summary>
[TestFixture]
public class HostEndpointMcpIntegrationTests
{
    [Test]
    public async Task AnMcpClient_ListsTheBuiltInAndTheEndpointTools_AndCallsOne()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        await using var host = await HostEndpointTestHost.StartAsync(
            withMcpServer: true,
            beforeMcp: x => x.Use(async (context, next) =>
            {
                // Stands in for JwtBearerAuthMiddleware on /beacon/mcp.
                context.User = HostEndpointTestHost.McpPrincipal(caller);
                context.Items[typeof(McpCaller)] = caller;
                await next(context);
            }));

        using var httpClient = host.App.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, "/beacon/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });
        var unknown = async () => await client.CallToolAsync("api_does_not_exist", new Dictionary<string, object?>());

        tools.Select(x => x.Name).Should().Contain(["get_context", "query", "search", "api_thing_by_id", "api_loan_grid"]);
        tools.Single(x => x.Name == "api_thing_by_id").ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
        result.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(result));
        result.StructuredContent!.Value.GetProperty("id").GetInt32().Should().Be(3);
        await unknown.Should().ThrowAsync<McpException>();
        host.AuditLogs.Should().Contain(x => x.Tool == "api_thing_by_id" && x.CallerHash == HostEndpointTestHost.CallerHash);
    }
}
