using Microsoft.AspNetCore.Http.HttpResults;
using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
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

        // Data migration is a MigrationOperator's (and an Admin's): its job catalogue, run history and runs. Creating,
        // running and deleting a job also need Query on its source and every step's data source and Write on the
        // destination (checked by the handlers).
        migrations.MapGet("/jobs", ([AsParameters] GetMigrationJobsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetMigrationJobs")
            .RequireBeaconPermission(BeaconPermissions.MigrationOperatorPolicy);

        migrations.MapGet("/jobs/{id:int}", async Task<Results<Ok<MigrationJobListItem>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var job = await m.Send(new GetMigrationJobQuery(id), ct);
                return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
            })
            .WithName("GetMigrationJob")
            .RequireBeaconPermission(BeaconPermissions.MigrationOperatorPolicy);

        migrations.MapPost("/jobs", ([FromBody] CreateMigrationJobCommand cmd, IMediator m, CancellationToken ct) =>
                m.Send(cmd, ct))
            .WithName("CreateMigrationJob")
            .RequireBeaconPermission(BeaconPermissions.MigrationOperatorPolicy);

        migrations.MapPost("/jobs/{id:int}/run", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new RunMigrationJobCommand(id), ct))
            .WithName("RunMigrationJob")
            .WithBeaconLongRunning()
            .RequireBeaconPermission(BeaconPermissions.MigrationOperatorPolicy);

        migrations.MapDelete("/jobs/{id:int}", (int id, [FromQuery] bool? forceDelete, IMediator m, CancellationToken ct) =>
                m.Send(new DeleteMigrationJobCommand(id, forceDelete ?? false), ct))
            .WithName("DeleteMigrationJob")
            .RequireBeaconPermission(BeaconPermissions.MigrationOperatorPolicy);

        migrations.MapGet("/executions", ([AsParameters] GetMigrationExecutionsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetMigrationExecutions")
            .RequireBeaconPermission(BeaconPermissions.MigrationOperatorPolicy);

        return group;
    }
}
