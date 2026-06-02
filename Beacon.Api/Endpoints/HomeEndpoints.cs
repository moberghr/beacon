using Beacon.Core.Handlers.Home;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class HomeEndpoints
{
    public static RouteGroupBuilder MapHomeEndpoints(this RouteGroupBuilder group)
    {
        var home = group.MapGroup("/home").WithTags("Home");

        home.MapGet("/trends", ([FromQuery] int? days, IMediator m, CancellationToken ct) =>
                m.Send(new GetHomeTrendsQuery(days ?? 30), ct))
            .WithName("GetHomeTrends");

        home.MapGet("/activity", ([FromQuery] int? limit, IMediator m, CancellationToken ct) =>
                m.Send(new GetHomeActivityQuery(limit ?? 10), ct))
            .WithName("GetHomeActivity");

        home.MapGet("/migration-summary", (IMediator m, CancellationToken ct) =>
                m.Send(new GetHomeMigrationSummaryQuery(), ct))
            .WithName("GetHomeMigrationSummary");

        home.MapGet("/task-summary", (IMediator m, CancellationToken ct) =>
                m.Send(new GetHomeTaskSummaryQuery(), ct))
            .WithName("GetHomeTaskSummary");

        home.MapGet("/uptime", ([FromQuery] int? hours, IMediator m, CancellationToken ct) =>
                m.Send(new GetExecutionUptimeQuery(hours ?? 24), ct))
            .WithName("GetExecutionUptime");

        return group;
    }
}
