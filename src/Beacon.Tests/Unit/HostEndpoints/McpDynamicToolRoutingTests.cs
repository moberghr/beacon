using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Mcp;
using Beacon.Core.SavedQueries;
using Beacon.MCP.Services;
using Beacon.Tests.Unit.SavedQueryTools;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// How the tools served beside the attribute tools (the saved-query and host endpoint tools) are listed and routed,
/// end to end through the MCP SDK: listing order, routing by name, fall-through to the next handler, a tool list that
/// fails rather than shortens when project access is undetermined, and the scope check ahead of every dynamic tool.
/// </summary>
[TestFixture]
public class McpDynamicToolRoutingTests
{
    private const string CustomToolName = "custom_tool";

    [Test]
    public async Task ToolsList_ListsTheSavedQueryToolsBeforeTheEndpointTools()
    {
        var fixture = new SavedQueryFixture();
        await using var host = await StartAsync(ExecuteCaller(), fixture);
        await using var client = await ConnectAsync(host);

        var names = (await client.ListToolsAsync())
            .Select(x => x.Name)
            .ToList();

        var lastSavedQuery = names.FindLastIndex(x => x.StartsWith("q_", StringComparison.Ordinal));
        var firstEndpoint = names.FindIndex(x => x.StartsWith("api_", StringComparison.Ordinal));
        lastSavedQuery.Should().BeGreaterThanOrEqualTo(0);
        firstEndpoint.Should().BeGreaterThan(lastSavedQuery, "the saved-query tools come first, then the host endpoint tools");
        names.Should().OnlyHaveUniqueItems();
    }

    [Test]
    public async Task ToolsCall_RoutesEachNameToTheSourceThatServesIt()
    {
        var fixture = new SavedQueryFixture();
        await using var host = await StartAsync(ExecuteCaller(), fixture);
        await using var client = await ConnectAsync(host);

        var saved = await client.CallToolAsync("q_loan_book_by_month", new Dictionary<string, object?> { ["from"] = "2026-01-01" });
        var endpoint = await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });

        saved.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(saved));
        saved.StructuredContent!.Value.GetProperty("rows")[0].GetProperty("total").GetInt32().Should().Be(42);
        endpoint.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(endpoint));
        endpoint.StructuredContent!.Value.GetProperty("id").GetInt32().Should().Be(3);
        fixture.Executor.Verify(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Once);
        host.AuditLogs.Select(x => x.Tool).Should().BeEquivalentTo(["q_loan_book_by_month", "api_thing_by_id"]);
    }

    [Test]
    public async Task ToolsCall_ForANameNoSourceServes_FallsThroughToTheNextHandler()
    {
        var fixture = new SavedQueryFixture();
        var customRan = false;
        await using var host = await StartAsync(ExecuteCaller(), fixture, x => x.Configure<McpServerOptions>(y =>
        {
            var previous = y.Handlers.CallToolHandler;
            y.Handlers.CallToolHandler = (request, cancellationToken) =>
            {
                if (request.Params?.Name != CustomToolName)
                {
                    return previous!(request, cancellationToken);
                }

                customRan = true;

                return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "custom" }] });
            };
        }));
        await using var client = await ConnectAsync(host);

        var custom = await client.CallToolAsync(CustomToolName, new Dictionary<string, object?>());
        var saved = await client.CallToolAsync("q_loan_book_by_month", new Dictionary<string, object?> { ["from"] = "2026-01-01" });
        var unknown = async () => await client.CallToolAsync("nobody_serves_this", new Dictionary<string, object?>());

        customRan.Should().BeTrue();
        HostEndpointDispatchTests.Text(custom).Should().Be("custom");
        saved.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(saved));
        (await unknown.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("nobody_serves_this");
    }

    [Test]
    public async Task AHostHandlerChainedBetweenAddBeaconMcpAndAddHostEndpointTools_SitsBetweenTheTwoSources()
    {
        var fixture = new SavedQueryFixture();
        var seen = new List<string>();
        await using var host = await StartAsync(ExecuteCaller(), fixture, x => x.Configure<McpServerOptions>(y =>
        {
            var previousList = y.Handlers.ListToolsHandler;
            var previousCall = y.Handlers.CallToolHandler;
            y.Handlers.ListToolsHandler = async (request, cancellationToken) =>
            {
                var result = await previousList!(request, cancellationToken);
                result.Tools = [.. result.Tools, new Tool { Name = CustomToolName }];

                return result;
            };
            y.Handlers.CallToolHandler = (request, cancellationToken) =>
            {
                seen.Add(request.Params!.Name);

                return previousCall!(request, cancellationToken);
            };
        }));
        await using var client = await ConnectAsync(host);

        var names = (await client.ListToolsAsync())
            .Select(x => x.Name)
            .ToList();
        await client.CallToolAsync("q_loan_book_by_month", new Dictionary<string, object?> { ["from"] = "2026-01-01" });
        await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });

        var custom = names.IndexOf(CustomToolName);
        custom.Should().BeGreaterThan(names.FindLastIndex(x => x.StartsWith("q_", StringComparison.Ordinal)));
        custom.Should().BeLessThan(names.FindIndex(x => x.StartsWith("api_", StringComparison.Ordinal)));
        seen.Should().Equal(["q_loan_book_by_month"], "the endpoint tools are served outside the host's handler, the saved-query tools inside it");
    }

    [Test]
    public async Task ToolsList_Fails_WhenTheCallersProjectAccessIsUndetermined()
    {
        var fixture = new SavedQueryFixture();
        var evaluator = new Mock<IProjectAccessEvaluator>();
        evaluator
            .Setup(x => x.GetScopeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("access store unavailable"));
        await using var host = await StartAsync(ExecuteCaller(), fixture, x => x.AddScoped(_ => evaluator.Object));
        await using var client = await ConnectAsync(host);

        var act = async () => await client.ListToolsAsync();

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain(ProjectContextFactory.AccessUndeterminedMessage);
        fixture.Source.Verify(x => x.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ReadScopedCaller_IsRefusedBeforeASavedQueryToolRuns_AndAudited()
    {
        var fixture = new SavedQueryFixture();
        var caller = new McpCaller(McpCallerKind.System, "reporting", HostEndpointTestHost.CallerHash, null, [HostEndpointTestHost.ProjectId], McpCallerScope.Read, [HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings)]);
        await using var host = await StartAsync(caller, fixture);
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("q_loan_book_by_month", new Dictionary<string, object?> { ["from"] = "2026-01-01" });

        result.IsError.Should().BeTrue();
        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        fixture.Executor.Verify(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), It.IsAny<int>(), It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()), Times.Never);
        var row = host.AuditLogs.Should().ContainSingle("the refused call reached no tool but is audited").Subject;
        row.Tool.Should().Be("q_loan_book_by_month");
        row.ErrorMessage.Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
    }

    private static McpCaller ExecuteCaller() =>
        HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings));

    private static Task<HostEndpointTestHost> StartAsync(McpCaller caller, SavedQueryFixture fixture, Action<IServiceCollection>? services = null) =>
        HostEndpointTestHost.StartAsync(
            withMcpServer: true,
            services: x =>
            {
                x.AddSingleton(fixture.Source.Object);
                x.AddSingleton(fixture.Executor.Object);
                services?.Invoke(x);
            },
            beforeMcp: x => x.Use(async (context, next) =>
            {
                // Stands in for JwtBearerAuthMiddleware on /beacon/mcp.
                context.User = HostEndpointTestHost.McpPrincipal(caller);
                context.Items[typeof(McpCaller)] = caller;
                await next(context);
            }));

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

    private sealed class SavedQueryFixture
    {
        public SavedQueryFixture()
        {
            var loanBook = SavedQueryTestData.Tool(
                "loan_book_by_month",
                [SavedQueryTestData.Step(1, 10, "SELECT month, total FROM loans WHERE issued >= {from}", SavedQueryTestData.Parameter("from", ParameterType.DateTime))],
                [HostEndpointTestHost.ProjectId]);
            Source
                .Setup(x => x.GetToolsAsync(It.IsAny<IReadOnlyCollection<int>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((IReadOnlyCollection<int> projects, CancellationToken _) =>
                    new[] { loanBook }
                        .Where(x => x.ProjectIds.Any(projects.Contains))
                        .ToList());
            Executor
                .Setup(x => x.ExecuteAsync(It.IsAny<SavedQueryToolDefinition>(), HostEndpointTestHost.ProjectId, It.IsAny<IReadOnlyDictionary<string, object?>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SavedQueryToolExecution(true, null, [new Dictionary<string, object?> { ["month"] = "2026-01", ["total"] = 42 }], false, 1000, ["loans"], 10));
        }

        public Mock<ISavedQueryToolSource> Source { get; } = new();

        public Mock<ISavedQueryToolExecutor> Executor { get; } = new();
    }
}
