using Microsoft.AspNetCore.Http.HttpResults;
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

        migrations.MapGet("/jobs", ([AsParameters] GetMigrationJobsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetMigrationJobs");

        migrations.MapGet("/jobs/{id:int}", async Task<Results<Ok<MigrationJobListItem>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var job = await m.Send(new GetMigrationJobQuery(id), ct);
                return job is null ? TypedResults.NotFound() : TypedResults.Ok(job);
            })
            .WithName("GetMigrationJob");

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

        migrations.MapGet("/executions", ([AsParameters] GetMigrationExecutionsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetMigrationExecutions");

        return group;
    }
}
