using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;
using Semantico.Core.Models.Projects;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;
using Semantico.Core.Services;

namespace Semantico.Web.Endpoints;

internal static class SemanticoEndpoints
{
    internal static void MapSemanticoEndpoints(this WebApplication app)
    {
        app.MapSubscriptionsEndpoints();
        app.MapQueriesEndpoints();
        app.MapProjectsEndpoints();
        app.MapNotificationsEndpoints();
    }

    private static void MapSubscriptionsEndpoints(this WebApplication app)
    {
        var apiGroup = app.MapGroup($"semantico/subscriptions")
            .WithTags("Subscriptions")
            .WithGroupName("semantico");

        apiGroup.MapGet("/get-subscriptions", async (
            [FromQuery] int? subscriptionId,
            [FromQuery] int? queryId,
            [FromQuery] NotificationType? notificationType,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetSubscriptions(subscriptionId, queryId, notificationType, null, cancellationToken);

            return response;
        });

        apiGroup.MapPost("/create-subscription", async (
            [FromBody] SubscriptionData subscription,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            await service.CreateSubscription(subscription, cancellationToken);
        });

        apiGroup.MapPatch("/update-subscription", async (
            [FromBody] SubscriptionData subscription,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            await service.UpdateSubscription(subscription, cancellationToken);
        });

        apiGroup.MapDelete("/delete-subscription/{subscriptionId}", async (
            [FromRoute] int subscriptionId,
            ISubscriptionService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteSubscription(subscriptionId, cancellationToken);
        });
    }

    private static void MapQueriesEndpoints(this WebApplication app)
    {
        var apiGroup = app.MapGroup($"semantico/queries")
            .WithTags("Queries")
            .WithGroupName("semantico");

        apiGroup.MapGet("/get-queries", async (
            [FromQuery] int? queryId,
            [FromQuery] int? projectId,
            [FromQuery] NotificationType? notificationType,
            IQueryService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetQueries(queryId, projectId, string.Empty, cancellationToken);

            return response;
        });

        apiGroup.MapPost("/create-query", async (
            [FromBody] QueryData query,
            IQueryService service,
            CancellationToken cancellationToken) =>
        {
            await service.CreateQuery(query, cancellationToken);
        });

        apiGroup.MapPatch("/update-query", async (
            [FromBody] QueryData query,
            IQueryService service,
            CancellationToken cancellationToken) =>
        {
            await service.UpdateQuery(query, cancellationToken);
        });

        apiGroup.MapDelete("/delete-query/{queryId}", async (
            [FromRoute] int queryId,
            IQueryService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteQuery(queryId, cancellationToken);
        });
    }

    private static void MapProjectsEndpoints(this WebApplication app)
    {
        var apiGroup = app.MapGroup($"semantico/projects")
            .WithTags("Projects")
            .WithGroupName("semantico");

        apiGroup.MapGet("/get-projects", async (
            [FromQuery] int? projectId,
            IProjectService service,
            CancellationToken cancellationToken) =>
        {
            var response = await service.GetProjects(projectId, cancellationToken);

            return response;
        });

        apiGroup.MapPost("/create-project", async (
            [FromBody] ProjectData project,
            IProjectService service,
            CancellationToken cancellationToken) =>
        {
            await service.CreateProject(project, cancellationToken);
        });

        apiGroup.MapPatch("/update-project", async (
            [FromBody] ProjectData project,
            IProjectService service,
            CancellationToken cancellationToken) =>
        {
            await service.UpdateProject(project, cancellationToken);
        });

        apiGroup.MapDelete("/delete-project/{projectId}", async (
            [FromRoute] int projectId,
            IProjectService service,
            CancellationToken cancellationToken) =>
        {
            await service.DeleteProject(projectId, cancellationToken);
        });
    }

    private static void MapNotificationsEndpoints(this WebApplication app)
    {
        var apiGroup = app.MapGroup($"semantico/notifications")
            .WithTags("Notifications")
            .WithGroupName("semantico");

        // apiGroup.MapGet("/get-query-execution-history", async (
        //     [FromRoute] GetQueryExecutionHistoryRequest request,
        //     INotificationService service,
        //     CancellationToken cancellationToken) =>
        // {
        //     var response = await service.GetQueryExecutionHistory(request, cancellationToken);
        //
        //     return response;
        // });
    }
}