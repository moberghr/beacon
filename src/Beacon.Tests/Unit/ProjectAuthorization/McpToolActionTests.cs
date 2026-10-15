using System.Reflection;
using System.Security.Claims;
using Beacon.AI.Services.Knowledge;
using Beacon.AI.Services.Mcp;
using Beacon.Core.Authorization;
using Beacon.Core.HostDocs;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
using Beacon.Core.Services.Providers;
using Beacon.Core.Services.Security;
using Beacon.Core.Services.Validation;
using Beacon.MCP.Services;
using Beacon.MCP.Tools;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Over <c>/beacon/mcp</c> each built-in tool needs its own project action in the project it resolves, as in the
/// playground: the catalog tools read the project, the SQL-carrying tools (<c>ask</c>, <c>query</c>, <c>dry_run</c>) work
/// ad hoc in it. A Viewer — a Viewer member of a restricted project, or a caller whose global role is Viewer — reads the
/// catalog but runs no SQL; every refusal is audited. The real actor accessor and evaluator run over in-memory rows (no
/// database): project 1 open, 2 restricted; user 11 a global Editor, user 13 a global Viewer.
/// </summary>
[TestFixture]
public class McpToolActionTests
{
    private const int OpenProject = 1;
    private const int RestrictedProject = 2;
    private const int EditorUserId = 11;
    private const int ViewerUserId = 13;

    private static readonly string[] SqlTools = ["ask", "query", "dry_run"];

    private ProjectAccessWorld _world = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(OpenProject);
        _world.Project(RestrictedProject, requiresMembership: true);
        _world.User(EditorUserId, "entra-editor");
        _world.User(ViewerUserId, "entra-viewer", RoleService.RoleLevels.Viewer);
    }

    [Test]
    public async Task AViewerMember_ReadsTheCatalog_ButIsRefusedTheSqlTools_AndEveryCallIsAudited()
    {
        _world.Member(RestrictedProject, EditorUserId, ProjectRole.Viewer);
        await using var host = await StartAsync(Caller(EditorUserId, RestrictedProject));
        await using var client = await ConnectAsync(host);

        var context = await client.CallToolAsync("get_context", Arguments(RestrictedProject));
        var search = await client.CallToolAsync("search", Arguments(RestrictedProject, ("query", "orders")));
        var refused = new Dictionary<string, CallToolResult>();
        foreach (var tool in SqlTools)
        {
            refused[tool] = await client.CallToolAsync(tool, Arguments(RestrictedProject, ("question", "how many orders?"), ("sql", "SELECT 1")));
        }

        context.IsError.Should().NotBe(true, Text(context));
        search.IsError.Should().NotBe(true, Text(search));
        foreach (var (tool, result) in refused)
        {
            result.IsError.Should().BeTrue(tool);
            Text(result).Should().Be($"Access denied: you may read project {RestrictedProject} but not run ad-hoc queries in it.", tool);
        }

        var audited = _world.AuditLogs.ToList();
        audited.Select(x => x.Tool).Should().BeEquivalentTo(["get_context", "search", "ask", "query", "dry_run"]);
        audited.Should().OnlyContain(x => x.UserId == EditorUserId);
        audited
            .Where(x => SqlTools.Contains(x.Tool))
            .Should()
            .OnlyContain(x => x.ErrorMessage != null && x.ErrorMessage.Contains("not run ad-hoc queries"));
    }

    [Test]
    public async Task AMappedUserCallerWhoseRoleIsViewer_IsRefusedTheSqlTools_EvenInAnOpenProject()
    {
        await using var host = await StartAsync(Caller(ViewerUserId, OpenProject));
        await using var client = await ConnectAsync(host);

        var context = await client.CallToolAsync("get_context", Arguments(OpenProject));
        var query = await client.CallToolAsync("query", Arguments(OpenProject, ("sql", "SELECT 1")));

        context.IsError.Should().NotBe(true, Text(context));
        query.IsError.Should().BeTrue();
        Text(query).Should().Be($"Access denied: you may read project {OpenProject} but not run ad-hoc queries in it.");
        _world.AuditLogs.Should().ContainSingle(x => x.Tool == "query" && x.ErrorMessage != null);
    }

    [Test]
    public async Task AnEditorMember_PassesTheProjectCheckOfTheSqlTools()
    {
        _world.Member(RestrictedProject, EditorUserId, ProjectRole.Editor);
        await using var host = await StartAsync(Caller(EditorUserId, RestrictedProject));
        await using var client = await ConnectAsync(host);

        // No data source is named, so each call stops at its own validation, past the project check.
        var query = await client.CallToolAsync("query", Arguments(RestrictedProject, ("sql", "SELECT 1")));
        var dryRun = await client.CallToolAsync("dry_run", Arguments(RestrictedProject, ("sql", "SELECT 1")));

        Text(query).Should().Be("Provide either datasource_name or datasource_id.");
        Text(dryRun).Should().NotContain("Access denied");
    }

    [Test]
    public async Task TheContext_ListsTheProjectsTheCallerMayQuery_AmongThoseItMayRead()
    {
        _world.Member(RestrictedProject, EditorUserId, ProjectRole.Viewer);

        var asEditor = await PopulateAsync(HostEndpointTestHost.McpPrincipal(Caller(EditorUserId, OpenProject, RestrictedProject)));
        var asViewer = await PopulateAsync(HostEndpointTestHost.McpPrincipal(Caller(ViewerUserId, OpenProject, RestrictedProject)));

        asEditor.AllowedProjectIds.Should().Equal(OpenProject, RestrictedProject);
        asEditor.QueryProjectIds.Should().Equal(OpenProject);
        asViewer.AllowedProjectIds.Should().Equal(OpenProject);
        asViewer.QueryProjectIds.Should().BeEmpty();
    }

    [TestCase("ask")]
    [TestCase("query")]
    [TestCase("dry_run")]
    [TestCase("feedback")]
    public void TheSqlToolsAndFeedback_ResolveOnlyAProjectTheCallerMayQuery(string tool)
    {
        var context = new McpProjectContext { AllowedProjectIds = [OpenProject, RestrictedProject], QueryProjectIds = [OpenProject] };

        var refused = ToolHelper.ResolveProjectId(context, tool, RestrictedProject, out var refusedId);
        var allowed = ToolHelper.ResolveProjectId(context, tool, OpenProject, out var allowedId);

        refused.Should().Be($"Access denied: you may read project {RestrictedProject} but not run ad-hoc queries in it.");
        refusedId.Should().Be(0);
        context.ActiveProjectId.Should().Be(OpenProject);
        allowed.Should().BeNull();
        allowedId.Should().Be(OpenProject);
    }

    [TestCase("get_context")]
    [TestCase("search")]
    [TestCase("get_documentation")]
    [TestCase("get_query_context")]
    public void TheCatalogTools_ResolveAnyProjectTheCallerMayRead(string tool)
    {
        var context = new McpProjectContext { AllowedProjectIds = [RestrictedProject], QueryProjectIds = [] };

        var error = ToolHelper.ResolveProjectId(context, tool, null, out var projectId);

        error.Should().BeNull();
        projectId.Should().Be(RestrictedProject);
    }

    [Test]
    public void EveryBuiltInTool_NeedsTheActionItsEntryNames_AndAnUnknownToolNeedsQuery()
    {
        // The explicit table: a new [McpServerTool] fails here until it is given an action deliberately.
        var expected = new Dictionary<string, ProjectAction>
        {
            ["get_context"] = ProjectAction.Read,
            ["search"] = ProjectAction.Read,
            ["get_documentation"] = ProjectAction.Read,
            ["get_query_context"] = ProjectAction.Read,
            ["ask"] = ProjectAction.Query,
            ["query"] = ProjectAction.Query,
            ["dry_run"] = ProjectAction.Query,
            ["feedback"] = ProjectAction.Query
        };
        var liveTools = typeof(Beacon.MCP.ServiceConfiguration).Assembly.GetTypes()
            .Where(x => x.GetCustomAttribute<McpServerToolTypeAttribute>() != null)
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
            .Select(x => x.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToList();

        liveTools.Should().BeEquivalentTo(expected.Keys, "every built-in tool has an entry in the table");
        liveTools.ToDictionary(x => x, McpToolProjectAction.Of).Should().Equal(expected);
        McpToolProjectAction.Of("a_tool_added_later").Should().Be(ProjectAction.Query, "a tool without an entry is held to the stricter action");
        McpToolProjectAction.Of("GET_CONTEXT").Should().Be(ProjectAction.Query, "names match exactly");
    }

    private static McpCaller Caller(int beaconUserId, params int[] projects)
    {
        return new McpCaller(McpCallerKind.User, "caller", HostEndpointTestHost.CallerHash, beaconUserId, projects, McpCallerScope.Execute, []);
    }

    private static Dictionary<string, object?> Arguments(int projectId, params (string Name, object? Value)[] more)
    {
        var arguments = new Dictionary<string, object?> { ["project_id"] = projectId };
        foreach (var (name, value) in more)
        {
            arguments[name] = value;
        }

        return arguments;
    }

    private async Task<HostEndpointTestHost> StartAsync(McpCaller caller)
    {
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        knowledgeGraph
            .Setup(x => x.SearchProjectAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        return await HostEndpointTestHost.StartAsync(
            withMcpServer: true,
            mapDefaultMinimalApis: false,
            beforeMcp: x => x.Use(async (context, next) =>
            {
                context.User = HostEndpointTestHost.McpPrincipal(caller);
                context.Items[typeof(McpCaller)] = caller;
                await next(context);
            }),
            services: x =>
            {
                _world.Register(x);
                x.AddSingleton(knowledgeGraph.Object);
                x.AddSingleton(Mock.Of<IProjectBriefService>());

                // The SQL tools' collaborators: a refused call never reaches them.
                x.AddSingleton(Mock.Of<IDataSourceProviderFactory>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IQueryGuardrailService>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<ISqlExecutionGate>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IConfiguredDatabaseResolver>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<ISharedSourceSettings>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IQueryCostLimit>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IQueryExecutionService>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<ICrossSourceQueryService>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IAskSqlPipeline>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IAskSqlExecutor>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IIntentClassifier>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IDataSourceRouter>(MockBehavior.Strict));
                x.AddSingleton(Mock.Of<IKnowledgeAnswerService>(MockBehavior.Strict));
            });
    }

    private async Task<McpProjectContext> PopulateAsync(ClaimsPrincipal user)
    {
        var services = new ServiceCollection();
        _world.Register(services);
        services.AddScoped<McpProjectContext>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await ProjectContextFactory.PopulateAsync(scope.ServiceProvider, CancellationToken.None);

        return scope.ServiceProvider.GetRequiredService<McpProjectContext>();
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

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(x => x.Text));
}
