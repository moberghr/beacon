using System.Net;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text;
using System.Text.RegularExpressions;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    private static IReadOnlyList<ProjectCheck> ChecksOf(Type request)
    {
        return ((IProjectAuthorizedRequest)RuntimeHelpers.GetUninitializedObject(request)).ProjectChecks.ToList();
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

    private static async Task<WebApplication> StartAsync(ResolvedProjectResource resolved)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddRouting();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddMediatR(x =>
        {
            x.RegisterServicesFromAssemblies(
                typeof(Beacon.Core.Handlers.Projects.GetProjectsQuery).Assembly,
                typeof(Beacon.AI.ServiceConfiguration).Assembly);
            x.AddOpenBehavior(typeof(ProjectAuthorizationBehavior<,>));
        });
        builder.Services.AddScoped(_ => TestProjectAccess.Authorizer(Viewer, new FixedResolver(resolved), new SqlCapture().Factory()));
        builder.Services.AddSingleton(Moq.Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Moq.Mock.Of<IBeaconUserContext>());
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

    private sealed class FixedResolver(ResolvedProjectResource resolved) : IProjectResourceResolver
    {
        public Task<ResolvedProjectResource> ResolveAsync(BeaconContext context, ProjectResource resource, CancellationToken cancellationToken) =>
            Task.FromResult(resolved);
    }
}
