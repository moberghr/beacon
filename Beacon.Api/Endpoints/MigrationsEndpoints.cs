using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataMigration;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class MigrationsEndpoints
{
    public static RouteGroupBuilder MapMigrationsEndpoints(this RouteGroupBuilder group)
    {
        var migrations = group.MapGroup("/migrations").WithTags("Migrations");

        migrations.MapGet("/jobs", (IMediator m, CancellationToken ct) =>
                m.Send(new GetMigrationJobsQuery(), ct))
            .WithName("GetMigrationJobs");

        migrations.MapPost("/jobs", ([FromBody] CreateMigrationJobCommand cmd, IMediator m, CancellationToken ct) =>
                m.Send(cmd, ct))
            .WithName("CreateMigrationJob")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        migrations.MapPost("/jobs/{id:int}/run", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new RunMigrationJobCommand(id), ct))
            .WithName("RunMigrationJob")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        migrations.MapDelete("/jobs/{id:int}", (int id, [FromQuery] bool? forceDelete, IMediator m, CancellationToken ct) =>
                m.Send(new DeleteMigrationJobCommand(id, forceDelete ?? false), ct))
            .WithName("DeleteMigrationJob")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        migrations.MapGet("/executions", (
                [FromQuery] int? migrationJobId,
                [FromQuery] MigrationStatus? status,
                [FromQuery] DateTime? startDate,
                [FromQuery] DateTime? endDate,
                [FromQuery] int? skip,
                [FromQuery] int? take,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetMigrationExecutionsQuery(
                    migrationJobId, status, startDate, endDate, skip ?? 0, take ?? 100), ct))
            .WithName("GetMigrationExecutions");

        return group;
    }
}
