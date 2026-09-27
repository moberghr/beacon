using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Core.Mcp;
using Beacon.Core.SavedQueries;
using Beacon.Tests.Unit.SavedQueryTools;
using Microsoft.Extensions.DependencyInjection;
using Moq;

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

    [Test]
    public async Task BuiltInEndpointAndSavedQueryTools_AllCoexist_AndASavedQueryToolRuns()
    {
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));
        var loanBook = SavedQueryTestData.Tool(
            "loan_book_by_month",
            [SavedQueryTestData.Step(1, 10, "SELECT month, total FROM loans WHERE issued >= {from}", SavedQueryTestData.Parameter("from", ParameterType.DateTime))],
            [HostEndpointTestHost.ProjectId]);
        var foreign = SavedQueryTestData.Tool("other_project", [SavedQueryTestData.Step(1, 11, "SELECT 1")], [99], queryId: 2);
        var source = new Mock<ISavedQueryToolSource>();
        source
            .Setup(x => x.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyCollection<int> projects, CancellationToken _) =>
                new[] { loanBook, foreign }
                    .Where(x => x.ProjectIds.Any(projects.Contains))
                    .ToList());
        var executor = new Mock<ISavedQueryToolExecutor>();
        executor
            .Setup(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), HostEndpointTestHost.ProjectId, It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SavedQueryToolExecution(true, null, [new Dictionary<string, object?> { ["month"] = "2026-01", ["total"] = 42 }], false, 1000, ["loans"], 10));

        await using var host = await HostEndpointTestHost.StartAsync(
            withMcpServer: true,
            services: x =>
            {
                x.AddSingleton(source.Object);
                x.AddSingleton(executor.Object);
            },
            beforeMcp: x => x.Use(async (context, next) =>
            {
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
        var saved = await client.CallToolAsync("q_loan_book_by_month", new Dictionary<string, object?> { ["from"] = "2026-01-01" });
        var endpoint = await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });

        tools.Select(x => x.Name).Should().Contain(["get_context", "query", "search", "api_thing_by_id", "q_loan_book_by_month"]);
        tools.Select(x => x.Name).Should().NotContain("q_other_project", "a tool of a project the caller is not authorized for is never listed");
        tools.Select(x => x.Name).Should().OnlyHaveUniqueItems();
        tools.Single(x => x.Name == "q_loan_book_by_month").ProtocolTool.Annotations!.ReadOnlyHint.Should().BeTrue();
        saved.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(saved));
        saved.StructuredContent!.Value.GetProperty("rows")[0].GetProperty("total").GetInt32().Should().Be(42);
        endpoint.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(endpoint));
        host.AuditLogs.Should().Contain(x => x.Tool == "q_loan_book_by_month" && x.ProjectId == HostEndpointTestHost.ProjectId);
        host.AuditLogs.Should().Contain(x => x.Tool == "api_thing_by_id");
    }
}
