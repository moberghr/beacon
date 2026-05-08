using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.DataMigration;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class MigrationsEndpoints
{
    public static RouteGroupBuilder MapMigrationsEndpoints(this RouteGroupBuilder group)
    {
        var migrations = group.MapGroup("/migrations").WithTags("Migrations");

        migrations.MapPost("/jobs", async (
                [FromBody] CreateMigrationJobCommand command,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(command, cancellationToken);
                return Results.Ok(result);
            })
            .WithName("CreateMigrationJob")
            .Produces<CreateMigrationJobResult>(StatusCodes.Status200OK);

        migrations.MapGet("/executions", async (
                [FromQuery] int? migrationJobId,
                [FromQuery] MigrationStatus? status,
                [FromQuery] DateTime? startDate,
                [FromQuery] DateTime? endDate,
                [FromQuery] int? skip,
                [FromQuery] int? take,
                IMediator mediator,
                CancellationToken cancellationToken) =>
            {
                var result = await mediator.Send(
                    new GetMigrationExecutionsQuery(
                        migrationJobId,
                        status,
                        startDate,
                        endDate,
                        skip ?? 0,
                        take ?? 100),
                    cancellationToken);
                return Results.Ok(result);
            })
            .WithName("GetMigrationExecutions")
            .Produces<GetMigrationExecutionsResult>(StatusCodes.Status200OK);

        return group;
    }
}
