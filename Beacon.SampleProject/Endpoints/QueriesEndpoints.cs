using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class QueriesEndpoints
{
    public static RouteGroupBuilder MapQueriesEndpoints(this RouteGroupBuilder group)
    {
        var queries = group.MapGroup("/queries").WithTags("Queries");

        queries.MapGet("/", async (
                [AsParameters] Beacon.Core.Services.GetQueriesRequest request,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetQueriesQuery { Request = request }, ct)))
            .WithName("GetQueries")
            .Produces<Beacon.Core.Helpers.PagedList<Beacon.Core.Models.Queries.QueryData>>(StatusCodes.Status200OK);

        queries.MapGet("/{id:int}", async (
                int id,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetQueryDetailQuery { QueryId = id }, ct)))
            .WithName("GetQueryDetail")
            .Produces<QueryDetailsData>(StatusCodes.Status200OK);

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

        queries.MapPut("/{id:int}", async (
                int id,
                QueryData body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new UpdateQueryCommand
                {
                    QueryId = id,
                    Query = body,
                }, ct);
                return Results.Ok(result);
            })
            .WithName("UpdateQuery")
            .Produces<UpdateQueryResult>(StatusCodes.Status200OK);

        queries.MapPost("/{id:int}/preview", async (
                int id,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ExecuteQueryPreviewCommand
                {
                    QueryId = id,
                }, ct)))
            .WithName("ExecuteQueryPreview")
            .Produces<QueryExecutionResult>(StatusCodes.Status200OK);

        queries.MapPost("/{id:int}/steps/{stepOrder:int}/preview", async (
                int id,
                int stepOrder,
                ExecuteStepPreviewRequest? body,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ExecuteStepPreviewCommand
                {
                    QueryId = id,
                    StepOrder = stepOrder,
                    Parameters = body?.Parameters,
                }, ct)))
            .WithName("ExecuteStepPreview")
            .Produces<QueryStepResult>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record ToggleQueryLockRequest(bool Lock, string? UserId);

internal sealed record ExecuteStepPreviewRequest(List<ParameterValue>? Parameters);
