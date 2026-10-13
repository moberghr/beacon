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
        group.MapGet("/queries/{queryId:int}/versions", ([AsParameters] GetQueryVersionsQuery query, IMediator m, CancellationToken ct) =>
                m.Send(query, ct))
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

        versions.MapPost("/{id:int}/restore", async (int id, IMediator m, CancellationToken ct) =>
        {
            // The restoring user is the request's caller, resolved by the handler — never an id the request carries.
            var result = await m.Send(new RestoreQueryVersionCommand { VersionId = id }, ct);
            return new RestoreQueryVersionResponse(result.VersionNumber, result.SubmittedForApproval, result.SupersededRequests);
        }).WithName("RestoreQueryVersion");

        return group;
    }
}

/// <param name="QueryId">The new version's number (named so since the first release).</param>
/// <param name="SubmittedForApproval">The approval workflow is on: the restore waits for approval and nothing went live.</param>
/// <param name="SupersededRequests">How many pending requests of the query the submitted restore replaced.</param>
internal sealed record RestoreQueryVersionResponse(int QueryId, bool SubmittedForApproval, int SupersededRequests);
