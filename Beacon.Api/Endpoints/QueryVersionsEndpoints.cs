using Beacon.Core.Handlers.QueryVersions;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class QueryVersionsEndpoints
{
    public static RouteGroupBuilder MapQueryVersionsEndpoints(this RouteGroupBuilder group)
    {
        // Versions for a specific query — sit under /queries to make the parent obvious.
        group.MapGet("/queries/{queryId:int}/versions", (int queryId, IMediator m, CancellationToken ct) =>
                m.Send(new GetQueryVersionsQuery { QueryId = queryId }, ct))
            .WithName("GetQueryVersions")
            .WithTags("QueryVersions");

        var versions = group.MapGroup("/query-versions").WithTags("QueryVersions");

        versions.MapGet("/{id:int}", async Task<Results<Ok<Beacon.Core.Models.Queries.QueryVersionDetail>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetQueryVersionDetailQuery { VersionId = id }, ct);
            return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        }).WithName("GetQueryVersionDetail");

        versions.MapGet("/diff", ([FromQuery] int versionIdA, [FromQuery] int versionIdB, IMediator m, CancellationToken ct) =>
                m.Send(new DiffQueryVersionsQuery { VersionIdA = versionIdA, VersionIdB = versionIdB }, ct))
            .WithName("DiffQueryVersions");

        versions.MapPost("/{id:int}/restore", async (int id, RestoreQueryVersionRequest body, IMediator m, CancellationToken ct) =>
        {
            var queryId = await m.Send(new RestoreQueryVersionCommand { VersionId = id, UserId = body.UserId }, ct);
            return new RestoreQueryVersionResponse(queryId);
        }).WithName("RestoreQueryVersion");

        return group;
    }
}

internal sealed record RestoreQueryVersionRequest(string? UserId);
internal sealed record RestoreQueryVersionResponse(int QueryId);
