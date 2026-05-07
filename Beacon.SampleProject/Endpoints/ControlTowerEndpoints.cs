using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ControlTower;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class ControlTowerEndpoints
{
    public static RouteGroupBuilder MapControlTowerEndpoints(this RouteGroupBuilder group)
    {
        var ct = group.MapGroup("/control-tower").WithTags("ControlTower");

        ct.MapGet("/statistics", async (IMediator mediator, CancellationToken cancellationToken) =>
                Results.Ok(await mediator.Send(new GetControlTowerStatisticsQuery(), cancellationToken)))
            .WithName("GetControlTowerStatistics")
            .Produces<GetControlTowerStatisticsResult>(StatusCodes.Status200OK);

        ct.MapGet("/health", async (
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromQuery] int? dataSourceId,
                [FromQuery] int? folderId,
                [FromQuery] HealthStatus? healthStatus,
                [FromQuery] bool? hasUnresolvedTasks,
                [FromQuery] string? searchKeyword,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new GetControlTowerHealthQuery(
                        page ?? 0,
                        pageSize ?? 100,
                        dataSourceId,
                        folderId,
                        healthStatus,
                        hasUnresolvedTasks,
                        searchKeyword),
                    cancellationToken);
                return Results.Ok(result);
            })
            .WithName("GetControlTowerHealth")
            .Produces<GetControlTowerHealthResult>(StatusCodes.Status200OK);

        return group;
    }
}
