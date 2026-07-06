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
using Microsoft.AspNetCore.Http.HttpResults;

namespace Beacon.Api.Endpoints;

internal static class DashboardsEndpoints
{
    public static RouteGroupBuilder MapDashboardsEndpoints(this RouteGroupBuilder group)
    {
        var dashboards = group.MapGroup("/dashboards").WithTags("Dashboards");

        dashboards.MapGet("/", ([AsParameters] GetDashboardsRequest request, IMediator m, CancellationToken ct) =>
                m.Send(new GetDashboardsQuery(request), ct))
            .WithName("GetDashboards");

        dashboards.MapGet("/{id:int}", async Task<Results<Ok<DashboardDetailsData>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var result = await m.Send(new GetDashboardQuery(id), ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }).WithName("GetDashboard");

        dashboards.MapPost("/", async (CreateDashboardCommand cmd, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(cmd, ct);
            return TypedResults.Created($"/beacon/api/dashboards/{result.DashboardId}", result);
        }).WithName("CreateDashboard");

        dashboards.MapPut("/{id:int}", async (int id, UpdateDashboardBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UpdateDashboardCommand(
                id, body.Name, body.Description, body.IsShared, body.IsDefault,
                body.RefreshIntervalSeconds, body.LayoutConfiguration), ct);
            return TypedResults.NoContent();
        }).WithName("UpdateDashboard");

        dashboards.MapDelete("/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteDashboardCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteDashboard");

        dashboards.MapPost("/{id:int}/widgets", (int id, AddWidgetBody body, IMediator m, CancellationToken ct) =>
                m.Send(new AddWidgetCommand(
                    id, body.Title, body.WidgetType, body.ConfigurationJson,
                    body.PositionX, body.PositionY, body.Width, body.Height,
                    body.RefreshIntervalSeconds), ct))
            .WithName("AddWidget");

        dashboards.MapDelete("/widgets/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new DeleteWidgetCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("DeleteWidget");

        dashboards.MapPost("/{id:int}/share", async (int id, ShareDashboardBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new ShareDashboardCommand(id, body.UserId, body.PermissionLevel), ct);
            return TypedResults.NoContent();
        }).WithName("ShareDashboard");

        dashboards.MapGet("/{id:int}/permissions", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetDashboardPermissionsQuery(id), ct))
            .WithName("GetDashboardPermissions");

        dashboards.MapDelete("/permissions/{id:int}", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RevokeDashboardPermissionCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("RevokeDashboardPermission");

        dashboards.MapPost("/{id:int}/clone", async (int id, CloneDashboardBody body, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new CloneDashboardCommand(id, body.NewName), ct);
            return TypedResults.Created($"/beacon/api/dashboards/{result.DashboardId}", result);
        }).WithName("CloneDashboard");

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
