using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Dashboards.AddWidget;
using Beacon.Core.Handlers.Dashboards.CloneDashboard;
using Beacon.Core.Handlers.Dashboards.CreateDashboard;
using Beacon.Core.Handlers.Dashboards.DeleteDashboard;
using Beacon.Core.Handlers.Dashboards.DeleteWidget;
using Beacon.Core.Handlers.Dashboards.GetDashboard;
using Beacon.Core.Handlers.Dashboards.GetDashboardPermissions;
using Beacon.Core.Handlers.Dashboards.GetDashboards;
using Beacon.Core.Handlers.Dashboards.RevokeDashboardPermission;
using Beacon.Core.Handlers.Dashboards.ShareDashboard;
using Beacon.Core.Handlers.Dashboards.UpdateDashboard;
using Beacon.Core.Models.Dashboards;
using MediatR;

namespace Beacon.SampleProject.Endpoints;

internal static class DashboardsEndpoints
{
    public static RouteGroupBuilder MapDashboardsEndpoints(this RouteGroupBuilder group)
    {
        var dashboards = group.MapGroup("/dashboards").WithTags("Dashboards");

        dashboards.MapGet("/", async ([AsParameters] GetDashboardsRequest request, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDashboardsQuery(request), ct)))
            .WithName("GetDashboards")
            .Produces<DashboardsListData>(StatusCodes.Status200OK);

        dashboards.MapGet("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetDashboardQuery(id), ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetDashboard")
            .Produces<DashboardDetailsData>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        dashboards.MapPost("/", async (CreateDashboardCommand command, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(command, ct);
                return Results.Created($"/beacon/api/dashboards/{result.DashboardId}", result);
            })
            .WithName("CreateDashboard")
            .Produces<CreateDashboardResult>(StatusCodes.Status201Created);

        dashboards.MapPut("/{id:int}", async (int id, UpdateDashboardBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UpdateDashboardCommand(
                    id, body.Name, body.Description, body.IsShared, body.IsDefault,
                    body.RefreshIntervalSeconds, body.LayoutConfiguration), ct);
                return Results.NoContent();
            })
            .WithName("UpdateDashboard")
            .Produces(StatusCodes.Status204NoContent);

        dashboards.MapDelete("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteDashboardCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("DeleteDashboard")
            .Produces(StatusCodes.Status204NoContent);

        dashboards.MapPost("/{id:int}/widgets", async (int id, AddWidgetBody body, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new AddWidgetCommand(
                    id, body.Title, body.WidgetType, body.ConfigurationJson,
                    body.PositionX, body.PositionY, body.Width, body.Height,
                    body.RefreshIntervalSeconds), ct);
                return Results.Ok(result);
            })
            .WithName("AddWidget")
            .Produces<AddWidgetResult>(StatusCodes.Status200OK);

        dashboards.MapDelete("/widgets/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new DeleteWidgetCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("DeleteWidget")
            .Produces(StatusCodes.Status204NoContent);

        dashboards.MapPost("/{id:int}/share", async (int id, ShareDashboardBody body, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new ShareDashboardCommand(id, body.UserId, body.PermissionLevel), ct);
                return Results.NoContent();
            })
            .WithName("ShareDashboard")
            .Produces(StatusCodes.Status204NoContent);

        dashboards.MapGet("/{id:int}/permissions", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetDashboardPermissionsQuery(id), ct)))
            .WithName("GetDashboardPermissions")
            .Produces<List<DashboardPermissionData>>(StatusCodes.Status200OK);

        dashboards.MapDelete("/permissions/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new RevokeDashboardPermissionCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("RevokeDashboardPermission")
            .Produces(StatusCodes.Status204NoContent);

        dashboards.MapPost("/{id:int}/clone", async (int id, CloneDashboardBody body, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new CloneDashboardCommand(id, body.NewName), ct);
                return Results.Created($"/beacon/api/dashboards/{result.DashboardId}", result);
            })
            .WithName("CloneDashboard")
            .Produces<CreateDashboardResult>(StatusCodes.Status201Created);

        return group;
    }
}

internal sealed record UpdateDashboardBody(
    string Name,
    string? Description,
    bool IsShared,
    bool IsDefault,
    int? RefreshIntervalSeconds,
    string? LayoutConfiguration);

internal sealed record AddWidgetBody(
    string Title,
    WidgetType WidgetType,
    string ConfigurationJson,
    int? PositionX,
    int? PositionY,
    int? Width,
    int? Height,
    int? RefreshIntervalSeconds);

internal sealed record ShareDashboardBody(string UserId, DashboardPermissionLevel PermissionLevel);
internal sealed record CloneDashboardBody(string NewName);
