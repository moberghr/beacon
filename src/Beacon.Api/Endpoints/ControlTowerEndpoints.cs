using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.ControlTower;
using Beacon.Core.Models.ControlTower;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class ControlTowerEndpoints
{
    public static RouteGroupBuilder MapControlTowerEndpoints(this RouteGroupBuilder group)
    {
        var ct = group.MapGroup("/control-tower").WithTags("ControlTower");

        ct.MapGet("/statistics", (
                [FromQuery] int? dataSourceId,
                [FromQuery] int? folderId,
                [FromQuery] HealthStatus? healthStatus,
                [FromQuery] bool? hasUnresolvedTasks,
                [FromQuery] string? searchKeyword,
                [FromQuery] int? timeRangeDays,
                IMediator m,
                CancellationToken cancellationToken) =>
                m.Send(
                    new GetControlTowerStatisticsQuery(
                        dataSourceId, folderId, healthStatus, hasUnresolvedTasks, searchKeyword,
                        timeRangeDays ?? 30),
                    cancellationToken))
            .WithName("GetControlTowerStatistics");

        ct.MapGet("/health", (
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                [FromQuery] int? dataSourceId,
                [FromQuery] int? folderId,
                [FromQuery] HealthStatus? healthStatus,
                [FromQuery] bool? hasUnresolvedTasks,
                [FromQuery] string? searchKeyword,
                [FromQuery] int? timeRangeDays,
                [FromQuery] ControlTowerSortBy? sortBy,
                IMediator m,
                CancellationToken cancellationToken) =>
                m.Send(
                    new GetControlTowerHealthQuery(
                        page ?? 0, pageSize ?? 100, dataSourceId, folderId, healthStatus,
                        hasUnresolvedTasks, searchKeyword, timeRangeDays ?? 30,
                        sortBy ?? ControlTowerSortBy.WorstFirst),
                    cancellationToken))
            .WithName("GetControlTowerHealth");

        ct.MapGet("/subscriptions/{id:int}/detail", (
                int id,
                [FromQuery] int? timeRangeDays,
                IMediator m,
                CancellationToken cancellationToken) =>
                m.Send(new GetControlTowerSubscriptionDetailQuery(id, timeRangeDays ?? 30), cancellationToken))
            .WithName("GetControlTowerSubscriptionDetail");

        return group;
    }
}
