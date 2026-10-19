using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// Every by-id route that runs a project check refuses a caller before the handler, over HTTP and through the real
/// MediatR pipeline: 404 when the resource lies in a project the caller cannot read, 403 when it can read it but the
/// route's action is refused. The handlers' own dependencies are not registered, so a request that reached its handler
/// would fail with a server error instead.
/// </summary>
[TestFixture]
public class ByIdRouteDenialTests
{
    private const int ReadOnlyProject = 1;
    private const int UnreadableProject = 3;

    /// <summary>A Viewer in project 1 that reaches nothing else.</summary>
    private static readonly ProjectAccessScope Viewer = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [ReadOnlyProject] = ProjectRole.Viewer },
        includesOpen: false,
        notOpen: []);

    /// <summary>An Editor in project 1 that reaches nothing else: it writes there, and curates nothing.</summary>
    private static readonly ProjectAccessScope Editor = TestProjectAccess.Scope(
        new Dictionary<int, ProjectRole> { [ReadOnlyProject] = ProjectRole.Editor },
        includesOpen: false,
        notOpen: []);

    [Test]
    public async Task EveryByIdRoute_DeniesWithoutProjectAccess_BeforeItsHandler()
    {
        var routes = (await BeaconApiRoutes.ReadAsync())
            .Where(x => x.IsById)
            .Distinct()
            .ToList();
        var requests = BeaconApiRoutes.RequestTypes();
        var checkedRoutes = routes
            .Select(x => (Route: x, Request: BeaconApiRoutes.RequestOf(x, requests)))
            .Where(x => x.Request != null && typeof(IProjectAuthorizedRequest).IsAssignableFrom(x.Request))
            .Where(x => ChecksOf(x.Request!).Any())
            .ToList();
        checkedRoutes.Should().HaveCountGreaterThan(50);

        await using var unreadable = await StartAsync(ResolvedProjectResource.InProject(UnreadableProject));
        await using var readOnly = await StartAsync(ResolvedProjectResource.InProject(ReadOnlyProject));
        var failures = new List<string>();

        foreach (var (route, request) in checkedRoutes)
        {
            var notFound = await SendAsync(unreadable, route);
            if (notFound != HttpStatusCode.NotFound)
            {
                failures.Add($"{route}: unreadable resource answered {(int)notFound}, not 404");
            }

            // A route whose action a Viewer may not take is refused with 403 on a resource it can read.
            if (ChecksOf(request!).Any(x => x.Action is not (ProjectAction.Read or ProjectAction.Run)))
            {
                var forbidden = await SendAsync(readOnly, route);
                if (forbidden != HttpStatusCode.Forbidden)
                {
                    failures.Add($"{route}: refused action answered {(int)forbidden}, not 403");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [Test]
    public async Task EveryCurationRoute_RefusesAnEditor_BeforeItsHandler()
    {
        var routes = (await BeaconApiRoutes.ReadAsync())
            .Where(x => x.IsById)
            .Distinct()
            .ToList();
        var requests = BeaconApiRoutes.RequestTypes();
        var curationRoutes = routes
            .Select(x => (Route: x, Request: BeaconApiRoutes.RequestOf(x, requests)))
            .Where(x => x.Request != null && typeof(IProjectAuthorizedRequest).IsAssignableFrom(x.Request))
            .Where(x => ChecksOf(x.Request!).Any(IsCuration))
            .Select(x => x.Route)
            .ToList();
        curationRoutes.Select(x => x.Name).Should().BeEquivalentTo(
            "UpdatePatternStatus",
            "ApplyDocumentationPatch",
            "RejectDocumentationPatch",
            "UpdateDocumentationSection",
            "GenerateProjectDocumentation",
            "VerifySchemaRelationship");

        await using var asEditor = await StartAsync(ResolvedProjectResource.InProject(ReadOnlyProject), Editor);
        var failures = new List<string>();
        foreach (var route in curationRoutes)
        {
            var status = await SendAsync(asEditor, route);
            if (status != HttpStatusCode.Forbidden)
            {
                failures.Add($"{route}: an Editor answered {(int)status}, not 403");
            }
        }

        failures.Should().BeEmpty();
    }

    // The scopes the real evaluator gives on an all-open deployment where user 7 is project 1's ProjectAdmin: the
    // ProjectAdmin passes every curation route's check (the request reaches its handler, whose dependencies are not
    // registered); the ProjectAdmin's own API key keeps the role's other actions and is refused every curation (403).
    [Test]
    public async Task EveryCurationRoute_PassesAProjectAdmin_AndRefusesItsApiKey()
    {
        var routes = (await BeaconApiRoutes.ReadAsync())
            .Where(x => x.IsById)
            .Distinct()
            .ToList();
        var requests = BeaconApiRoutes.RequestTypes();
        var curationRoutes = routes
            .Where(x => BeaconApiRoutes.RequestOf(x, requests) is { } request
                && typeof(IProjectAuthorizedRequest).IsAssignableFrom(request)
                && ChecksOf(request).Any(IsCuration))
            .ToList();
        var factory = OpenDeployment();
        var projectAdmin = new BeaconActor("ext-ada", false) { Kind = BeaconPrincipalKind.User, BeaconUserId = 7, IsActiveUser = true, RoleLevel = 2 };
        var apiKey = projectAdmin with { Kind = BeaconPrincipalKind.ApiKey, IsApiKey = true, ScopeCeiling = Beacon.Core.Mcp.McpCallerScope.Execute };
        var projectAdminScope = await TestProjectAccess.RealEvaluator(projectAdmin, factory).GetScopeAsync(CancellationToken.None);
        var apiKeyScope = await TestProjectAccess.RealEvaluator(apiKey, factory).GetScopeAsync(apiKey, CancellationToken.None);
        apiKeyScope.Allows(ReadOnlyProject, ProjectAction.Manage).Should().BeTrue("the key acts as its ProjectAdmin owner otherwise");

        await using var asProjectAdmin = await StartAsync(ResolvedProjectResource.InProject(ReadOnlyProject), projectAdminScope);
        await using var asApiKey = await StartAsync(ResolvedProjectResource.InProject(ReadOnlyProject), apiKeyScope);
        var failures = new List<string>();
        foreach (var route in curationRoutes)
        {
            if (!await ReachesHandlerAsync(asProjectAdmin, route))
            {
                failures.Add($"{route}: the ProjectAdmin was refused");
            }

            var refused = await SendAsync(asApiKey, route);
            if (refused != HttpStatusCode.Forbidden)
            {
                failures.Add($"{route}: the ProjectAdmin's API key answered {(int)refused}, not 403");
            }
        }

        curationRoutes.Should().HaveCount(6);
        failures.Should().BeEmpty();
    }

    private static bool IsCuration(ProjectCheck check)
    {
        return check.Action == ProjectAction.Curate
            || (check.InEveryLinkingProject && check.EveryLinkingProjectAction == ProjectAction.Curate);
    }

    private static IReadOnlyList<ProjectCheck> ChecksOf(Type request)
    {
        return ((IProjectAuthorizedRequest)RuntimeHelpers.GetUninitializedObject(request)).ProjectChecks.ToList();
    }

    // A request that passes the route's checks reaches its handler, whose dependencies are not registered: the test server
    // rethrows the handler's activation failure.
    private static async Task<bool> ReachesHandlerAsync(WebApplication app, BeaconApiRoute route)
    {
        try
        {
            var status = await SendAsync(app, route);

            return status is not (HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
        }
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("Unable to resolve service", StringComparison.Ordinal))
        {
            return true;
        }
    }

    private static async Task<HttpStatusCode> SendAsync(WebApplication app, BeaconApiRoute route)
    {
        // Route values are 5; the one required query value (the export's format) is given to every route.
        var path = Regex.Replace(route.Pattern, "\\{[^}]+\\}", "5") + "?asHtml=false";
        using var message = new HttpRequestMessage(new HttpMethod(route.Method), path);
        if (route.Method is "POST" or "PUT")
        {
            message.Content = new StringContent(Body(route), Encoding.UTF8, "application/json");
        }

        using var response = await app.GetTestClient().SendAsync(message);

        return response.StatusCode;
    }

    // The smallest bodies the routes bind; the check runs before the handler, so their content does not matter.
    private static string Body(BeaconApiRoute route)
    {
        return route.Name switch
        {
            "UpdateQuery" => """{"name":"q","steps":[]}""",
            "UpdateDataContract" => """{"dataSourceId":5,"schemaName":"s","tableName":"t","name":"c","cronExpression":"0 * * * *","isEnabled":true,"alertOnFailure":false,"failureThresholdScore":50,"rules":[]}""",
            "SetProjectMember" => """{"role":1}""",
            "LinkProjectDataSources" => """{"dataSourceIds":[5]}""",
            "AddSubscriptionRecipients" => """{"recipientIds":[1]}""",
            "UpdateSchemaRelationship" => """{"label":"l","cardinality":1}""",
            "VerifySchemaRelationship" => """{"isVerified":true}""",
            "UpdateDocumentationSection" => """{"content":"c"}""",
            "InstructDocumentation" => """{"instruction":"i"}""",
            "RefineAiActor" => """{"feedback":"f"}""",
            "AddTaskComment" => """{"content":"c"}""",
            "ResolveTask" => """{"resolutionNotes":"n"}""",
            "SetTaskPriority" => """{"priority":1}""",
            "UpdatePatternStatus" => """{"newStatus":1}""",
            "RejectAiActorPlan" => """{"reason":"r"}""",
            "RequestPlanRevision" => """{"feedback":"f"}""",
            "CreateSchemaRelationship" => """{"sourceSchema":"s","sourceTable":"t","sourceColumn":"c","targetSchema":"s","targetTable":"u","targetColumn":"d","cardinality":1}""",
            _ => "{}"
        };
    }

    private static async Task<WebApplication> StartAsync(ResolvedProjectResource resolved, ProjectAccessScope? scope = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddWebRequestPrincipal();
        builder.Services.AddMediatR(x =>
        {
            x.RegisterServicesFromAssemblies(
                typeof(Beacon.Core.Handlers.Projects.GetProjectsQuery).Assembly,
                typeof(Beacon.AI.ServiceConfiguration).Assembly);
            x.AddOpenBehavior(typeof(ProjectAuthorizationBehavior<,>));
        });
        builder.Services.AddScoped(_ => TestProjectAccess.Authorizer(scope ?? Viewer, new FixedResolver(resolved), new SqlCapture().Factory()));
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        var app = builder.Build();

        // The route policies pass (an Admin-role session); project access comes from the authorizer's fixed scope alone.
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada"), new Claim(ClaimTypes.Role, "Admin")], "Test"));
            await next(context);
        });
        app.UseRouting();
        app.UseAuthorization();
        var api = app.MapGroup("/beacon/api")
            .AddEndpointFilter<BeaconAccessExceptionEndpointFilter>();
        api.MapProjectsEndpoints();
        api.MapQueriesEndpoints();
        api.MapQueryFoldersEndpoints();
        api.MapQueryVersionsEndpoints();
        api.MapApprovalsEndpoints();
        api.MapDataQualityEndpoints();
        api.MapMcpManagementEndpoints();
        api.MapAiActorsEndpoints();
        api.MapNotificationsEndpoints();
        api.MapControlTowerEndpoints();
        api.MapSubscriptionsEndpoints();
        api.MapDataSourcesEndpoints();
        api.MapSchemaRelationshipsEndpoints();
        api.MapTasksEndpoints();
        api.MapEvalEndpoints();
        await app.StartAsync();

        return app;
    }

    // Open projects 1 and 2, data source 10 linked to project 1, user 7 project 1's ProjectAdmin: every project open and
    // every data source reachable, so a user's scope is on the fast path.
    private static IDbContextFactory<BeaconContext> OpenDeployment()
    {
        var projects = new[] { 1, 2 }
            .Select(x => new Project { Id = x, Name = $"project {x}" })
            .ToList();
        var source = new DataSource { Id = 10, Name = "sales", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" };
        var links = new List<ProjectDataSource> { new() { ProjectId = 1, DataSourceId = 10, Project = projects[0], DataSource = source } };
        var members = new List<ProjectMember> { new() { ProjectId = 1, UserId = 7, Role = ProjectRole.ProjectAdmin } };
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, []),
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet([source], []),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, []),
                    [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(members, [])
                },
                []));

        return factory.Object;
    }

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resolved);
    }
}
