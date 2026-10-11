using System.Security.Claims;
using Beacon.AI.Services.Knowledge;
using Beacon.Core.Authorization;
using Beacon.Core.Data.Entities;
using Beacon.Core.HostDocs;
using Beacon.Core.Mcp;
using Beacon.Core.Services;
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
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// What an MCP caller reaches comes from the project access evaluator, decided per call: the MCP context lists the
/// projects the caller may read. An API key without a project list reaches its owner's projects; a mapped user caller its
/// configured projects within its user's access; a mapped user caller without a Beacon user its configured open
/// projects; a system caller its configured projects. A browser session gets no projects on the MCP route, and every
/// caller is recorded by its Beacon user. The real actor accessor and evaluator run over in-memory rows (no database):
/// project 1 open, 2 restricted, 3 restricted, 4 open, 5 archived; user 7 (an Editor) is a Viewer member of project 2;
/// user 11 is an Editor without memberships; user 12 a signed-in Editor.
/// </summary>
[TestFixture]
public class McpProjectAccessTests
{
    private const int OpenProject = 1;
    private const int RestrictedProject = 2;
    private const int OtherRestrictedProject = 3;
    private const int OtherOpenProject = 4;
    private const int ArchivedProject = 5;
    private const int KeyOwnerId = 7;
    private const int CallerUserId = 11;
    private const int SessionUserId = 12;

    private ProjectAccessWorld _world = null!;

    [SetUp]
    public void SetUp()
    {
        _world = new ProjectAccessWorld();
        _world.Project(OpenProject);
        _world.Project(RestrictedProject, requiresMembership: true);
        _world.Project(OtherRestrictedProject, requiresMembership: true);
        _world.Project(OtherOpenProject);
        _world.Project(ArchivedProject).Archive();
        _world.User(KeyOwnerId, "ext-owner");
        _world.User(CallerUserId, "entra-oid");
        _world.User(SessionUserId, "ext-ada");
        _world.Member(RestrictedProject, KeyOwnerId, ProjectRole.Viewer);
    }

    [Test]
    public async Task AKeyWithoutAProjectList_ReachesItsOwnersProjects_ButNoRestrictedProjectTheOwnerIsNotIn()
    {
        var context = await PopulateAsync(ApiKey(projects: null));

        context.AllowedProjectIds.Should().Equal(OpenProject, RestrictedProject, OtherOpenProject);
        context.UserId.Should().Be(KeyOwnerId);
        context.ApiKeyId.Should().Be(3);
    }

    [Test]
    public async Task AKeyWithoutAProjectList_WhereNothingIsRestricted_ReachesEveryLiveProject()
    {
        _world.Projects.RemoveAll(x => x.RequiresMembership || x.ArchivedTime != null);

        var context = await PopulateAsync(ApiKey(projects: null));

        context.AllowedProjectIds.Should().Equal(OpenProject, OtherOpenProject);
        context.QueryProjectIds.Should().Equal([OpenProject, OtherOpenProject], "the key's owner is an Editor, who may work ad hoc in an open project");
    }

    [Test]
    public async Task TheLiveProjects_AKeyWithoutAProjectListReaches_LeaveArchivedProjectsOut()
    {
        // Nothing is restricted, so the context lists the live projects as the database returns them: the read carries
        // the archived filter. The owner is an Editor in project 1 and a Viewer in project 4, so only project 1 allows
        // ad-hoc queries.
        var capture = new SqlCapture().ThenRows([OpenProject], [OtherOpenProject]);
        var editor = new ProjectGrant(ProjectRole.Editor, new HashSet<ProjectAction> { ProjectAction.Read, ProjectAction.Run, ProjectAction.Query, ProjectAction.Write });
        var viewer = new ProjectGrant(ProjectRole.Viewer, new HashSet<ProjectAction> { ProjectAction.Read });
        var everywhere = new ProjectAccessScope(
            false,
            Enum.GetValues<ProjectAction>().ToDictionary(x => x, x => x == ProjectAction.ManageMembers ? ProjectSet.Empty : ProjectSet.All),
            new Dictionary<int, ProjectGrant> { [OpenProject] = editor, [OtherOpenProject] = viewer });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<McpProjectContext>();
        services.AddSingleton(capture.Factory());
        services.AddSingleton(TestProjectAccess.Evaluator(everywhere));
        services.AddSingleton(TestProjectAccess.Actors(KeyOwnerId, "ext-owner"));
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = ApiKey(projects: null) } });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await ProjectContextFactory.PopulateAsync(scope.ServiceProvider, CancellationToken.None);

        var context = scope.ServiceProvider.GetRequiredService<McpProjectContext>();
        capture.Commands.Should().ContainSingle().Which.Should().Contain("p.archived_time IS NULL");
        context.AllowedProjectIds.Should().Equal(OpenProject, OtherOpenProject);
        context.QueryProjectIds.Should().Equal(OpenProject);
        context.AccessUndetermined.Should().BeFalse();
    }

    [Test]
    public async Task ACallerWhoseProjectAccessCannotBeDetermined_IsToldToRetry_NotThatItHasNoProjects()
    {
        var context = await PopulateAsync(ApiKey(projects: null), FailingEvaluator());

        var explicitError = ToolHelper.ResolveProjectId(context, "get_context", OpenProject, out var explicitProject);
        var implicitError = ToolHelper.ResolveProjectId(context, "get_context", null, out var implicitProject);

        context.AccessUndetermined.Should().BeTrue();
        context.AllowedProjectIds.Should().BeEmpty("the context still reaches no project");
        context.QueryProjectIds.Should().BeEmpty();
        explicitError.Should().Be(ProjectContextFactory.AccessUndeterminedMessage);
        implicitError.Should().Be(ProjectContextFactory.AccessUndeterminedMessage);
        explicitProject.Should().Be(0);
        implicitProject.Should().Be(0);
    }

    [Test]
    public async Task OverMcp_ACallerWhoseProjectAccessCannotBeDetermined_GetsAFailedToolList_AndAnAuditedRetryAnswer()
    {
        var caller = new McpCaller(McpCallerKind.User, "Ada", HostEndpointTestHost.CallerHash, CallerUserId, [OpenProject], McpCallerScope.Execute, []);
        await using var host = await HostEndpointTestHost.StartAsync(
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
                x.AddScoped(_ => FailingEvaluator());
            });
        await using var client = await ConnectAsync(host);

        var list = async () => await client.ListToolsAsync();
        var call = await client.CallToolAsync("search", new Dictionary<string, object?> { ["query"] = "orders", ["project_id"] = OpenProject });

        (await list.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain(ProjectContextFactory.AccessUndeterminedMessage);
        call.IsError.Should().BeTrue();
        Text(call).Should().Be(ProjectContextFactory.AccessUndeterminedMessage);
        _world.AuditLogs.Should().ContainSingle(x => x.Tool == "search")
            .Which.Should().Match<McpAuditLog>(x => x.UserId == CallerUserId && x.ErrorMessage != null);
    }

    [Test]
    public async Task AKeyWithAProjectList_ReachesTheListedProjectsItsOwnerReaches()
    {
        var context = await PopulateAsync(ApiKey(projects: [OpenProject, OtherRestrictedProject, ArchivedProject]));

        context.AllowedProjectIds.Should().Equal(OpenProject);
    }

    [Test]
    public async Task AMappedUserCaller_ReachesItsConfiguredProjects_WithinItsUsersMemberships()
    {
        _world.Member(RestrictedProject, CallerUserId, ProjectRole.Viewer);

        var context = await PopulateAsync(McpCaller(McpCallerKind.User, CallerUserId, [OpenProject, RestrictedProject, OtherRestrictedProject]));

        context.AllowedProjectIds.Should().Equal(OpenProject, RestrictedProject);
        context.UserId.Should().Be(CallerUserId);
    }

    [Test]
    public async Task AMappedUserCallerWithoutABeaconUser_ReachesItsConfiguredOpenProjectsOnly()
    {
        var context = await PopulateAsync(McpCaller(McpCallerKind.User, beaconUserId: null, [OpenProject, RestrictedProject]));

        context.AllowedProjectIds.Should().Equal(OpenProject);
        context.UserId.Should().BeNull();
    }

    [Test]
    public async Task AMappedSystemCaller_ReachesItsConfiguredProjects_RestrictedOrNot()
    {
        var context = await PopulateAsync(McpCaller(McpCallerKind.System, beaconUserId: null, [RestrictedProject, OtherRestrictedProject]));

        context.AllowedProjectIds.Should().Equal(RestrictedProject, OtherRestrictedProject);
    }

    [Test]
    public async Task AMappedCallerOfADisabledUser_ReachesNoProject_FromItsNextCall()
    {
        // The caller mapping is cached for a minute and still names the user; project access reads the user's live state.
        _world.Member(RestrictedProject, CallerUserId, ProjectRole.Viewer);
        _world.Users.First(x => x.Id == CallerUserId).IsEnabled = false;

        var context = await PopulateAsync(McpCaller(McpCallerKind.User, CallerUserId, [OpenProject, RestrictedProject]));

        context.AllowedProjectIds.Should().BeEmpty();
        context.UserId.Should().BeNull();
    }

    [Test]
    public async Task ABrowserSession_GetsNoProjectsOnTheMcpRoute_AndIsRecordedByItsBeaconUser()
    {
        var session = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ext-ada"), new Claim(McpCallerClaimTypes.AllowedProjects, "[1]")],
            "Cookies"));

        var context = await PopulateAsync(session);

        context.AllowedProjectIds.Should().BeEmpty();
        context.UserId.Should().Be(SessionUserId, "the session's NameIdentifier is its external id, never a Beacon user id");
    }

    [Test]
    public async Task AMembershipRevokedBetweenTwoCallsOfOneSession_DeniesTheSecondCall()
    {
        var member = _world.Member(RestrictedProject, CallerUserId, ProjectRole.Viewer);
        var caller = new McpCaller(McpCallerKind.User, "Ada", HostEndpointTestHost.CallerHash, CallerUserId, [RestrictedProject], McpCallerScope.Execute, []);
        var knowledgeGraph = new Mock<IKnowledgeGraphService>();
        knowledgeGraph
            .Setup(x => x.GetProjectDataSourcesAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        await using var host = await HostEndpointTestHost.StartAsync(
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
            });
        await using var client = await ConnectAsync(host);
        var arguments = new Dictionary<string, object?> { ["project_id"] = RestrictedProject };

        var first = await client.CallToolAsync("get_context", arguments);
        _world.Members.Remove(member);
        var second = await client.CallToolAsync("get_context", arguments);

        first.IsError.Should().NotBe(true, Text(first));
        Text(first).Should().StartWith($"# Project: Project {RestrictedProject}");
        second.IsError.Should().BeTrue();
        Text(second).Should().Be($"Access denied: you do not have access to project {RestrictedProject}.");
        var audit = _world.AuditLogs
            .Where(x => x.Tool == "get_context")
            .ToList();
        audit.Should().HaveCount(2, "both calls are audited");
        audit.Should().OnlyContain(x => x.UserId == CallerUserId);
        audit[1].ErrorMessage.Should().NotBeNull();
    }

    [Test]
    public async Task AHostEndpointTool_ReachesARestrictedProject_OnlyWhileTheUserCallerIsAMember()
    {
        // The host's endpoint tools belong to its project; a user caller configured for it still needs membership.
        _world.Project(HostEndpointTestHost.ProjectId, requiresMembership: true);
        var caller = new McpCaller(
            McpCallerKind.User,
            "Ada",
            HostEndpointTestHost.CallerHash,
            CallerUserId,
            [HostEndpointTestHost.ProjectId],
            McpCallerScope.Execute,
            [HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings)]);
        await using var host = await HostEndpointTestHost.StartAsync(
            withMcpServer: true,
            beforeMcp: x => x.Use(async (context, next) =>
            {
                context.User = HostEndpointTestHost.McpPrincipal(caller);
                context.Items[typeof(McpCaller)] = caller;
                await next(context);
            }),
            services: _world.Register);
        await using var client = await ConnectAsync(host);
        var arguments = new Dictionary<string, object?> { ["name"] = "ada" };

        var asNonMember = await client.CallToolAsync("api_minimal_echo", arguments);
        _world.Member(HostEndpointTestHost.ProjectId, CallerUserId, ProjectRole.Viewer);
        var asMember = await client.CallToolAsync("api_minimal_echo", arguments);

        asNonMember.IsError.Should().BeTrue();
        Text(asNonMember).Should().Be($"Access denied: you do not have access to project {HostEndpointTestHost.ProjectId}.");
        asMember.IsError.Should().NotBe(true, Text(asMember));
        Text(asMember).Should().Contain("ada");
    }

    [Test]
    public async Task ResolvingAProject_WithAContextNobodyFilled_IsDenied()
    {
        var context = new McpProjectContext();

        var explicitError = ToolHelper.ResolveProjectId(context, "get_context", OpenProject, out _);
        var implicitError = ToolHelper.ResolveProjectId(context, "get_context", null, out _);

        explicitError.Should().Be($"Access denied: you do not have access to project {OpenProject}.");
        implicitError.Should().StartWith("No projects are available");
    }

    private async Task<McpProjectContext> PopulateAsync(ClaimsPrincipal user, IProjectAccessEvaluator? evaluator = null)
    {
        var services = new ServiceCollection();
        _world.Register(services);
        if (evaluator != null)
        {
            services.AddScoped(_ => evaluator);
        }

        services.AddScoped<McpProjectContext>();
        services.AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = user } });
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        await ProjectContextFactory.PopulateAsync(scope.ServiceProvider, CancellationToken.None);

        return scope.ServiceProvider.GetRequiredService<McpProjectContext>();
    }

    // An evaluator whose read fails (the database is unreachable).
    private static IProjectAccessEvaluator FailingEvaluator()
    {
        var evaluator = new Mock<IProjectAccessEvaluator>();
        evaluator
            .Setup(x => x.GetScopeAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("The database could not be reached."));

        return evaluator.Object;
    }

    // The principal ApiKeyAuthMiddleware mints for Execute key 3 of the key owner.
    private static ClaimsPrincipal ApiKey(IReadOnlyList<int>? projects)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, KeyOwnerId.ToString()),
            new(McpCallerClaimTypes.ApiKeyId, "3"),
            new(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod),
            new(McpCallerClaimTypes.Scope, BeaconScopes.Execute)
        };
        if (projects != null)
        {
            claims.Add(new Claim(McpCallerClaimTypes.AllowedProjects, $"[{string.Join(",", projects)}]"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpCallerClaimTypes.ApiKeyAuthenticationType));
    }

    private static ClaimsPrincipal McpCaller(McpCallerKind kind, int? beaconUserId, IReadOnlyList<int> configured)
    {
        return HostEndpointTestHost.McpPrincipal(
            new McpCaller(kind, "caller", HostEndpointTestHost.CallerHash, beaconUserId, configured, McpCallerScope.Execute, []));
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
