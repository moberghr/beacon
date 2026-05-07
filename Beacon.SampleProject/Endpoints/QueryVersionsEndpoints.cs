using Beacon.Core.Handlers.QueryVersions;
using Beacon.Core.Models.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class QueryVersionsEndpoints
{
    public static RouteGroupBuilder MapQueryVersionsEndpoints(this RouteGroupBuilder group)
    {
        // Versions for a specific query — sit under /queries to make the parent obvious.
        group.MapGet("/queries/{queryId:int}/versions", async (int queryId, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetQueryVersionsQuery { QueryId = queryId }, ct)))
            .WithName("GetQueryVersions")
            .WithTags("QueryVersions")
            .Produces<List<QueryVersionSummary>>(StatusCodes.Status200OK);

        var versions = group.MapGroup("/query-versions").WithTags("QueryVersions");

        versions.MapGet("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetQueryVersionDetailQuery { VersionId = id }, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetQueryVersionDetail")
            .Produces<QueryVersionDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        versions.MapGet("/diff", async (
                [FromQuery] int versionIdA,
                [FromQuery] int versionIdB,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new DiffQueryVersionsQuery
                {
                    VersionIdA = versionIdA,
                    VersionIdB = versionIdB,
                }, ct)))
            .WithName("DiffQueryVersions")
            .Produces<QueryVersionDiff>(StatusCodes.Status200OK);

        versions.MapPost("/{id:int}/restore", async (
                int id,
                RestoreQueryVersionRequest body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var queryId = await mediator.Send(new RestoreQueryVersionCommand
                {
                    VersionId = id,
                    UserId = body.UserId,
                }, ct);
                return Results.Ok(new RestoreQueryVersionResponse(queryId));
            })
            .WithName("RestoreQueryVersion")
            .Produces<RestoreQueryVersionResponse>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record RestoreQueryVersionRequest(string? UserId);
internal sealed record RestoreQueryVersionResponse(int QueryId);
