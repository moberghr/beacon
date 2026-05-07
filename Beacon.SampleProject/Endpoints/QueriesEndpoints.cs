using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class QueriesEndpoints
{
    public static RouteGroupBuilder MapQueriesEndpoints(this RouteGroupBuilder group)
    {
        var queries = group.MapGroup("/queries").WithTags("Queries");

        queries.MapPost("/{id:int}/lock", async (
                int id,
                ToggleQueryLockRequest body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new ToggleQueryLockCommand
                {
                    QueryId = id,
                    Lock = body.Lock,
                    UserId = body.UserId,
                }, ct);
                return Results.Ok(result);
            })
            .WithName("ToggleQueryLock")
            .Produces<ToggleQueryLockResult>(StatusCodes.Status200OK);

        queries.MapGet("/{id:int}/change-history", async (
                int id,
                [FromQuery] int? stepId,
                [FromQuery] ChangeSource? changeSource,
                [FromQuery] DateTime? fromDate,
                [FromQuery] DateTime? toDate,
                [FromQuery] int? maxResults,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetQueryChangeHistoryQuery
                {
                    QueryId = id,
                    StepId = stepId,
                    ChangeSource = changeSource,
                    FromDate = fromDate,
                    ToDate = toDate,
                    MaxResults = maxResults ?? 50,
                }, ct)))
            .WithName("GetQueryChangeHistory")
            .Produces<GetQueryChangeHistoryResult>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record ToggleQueryLockRequest(bool Lock, string? UserId);
