using System.Security.Claims;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class QueriesEndpoints
{
    public static RouteGroupBuilder MapQueriesEndpoints(this RouteGroupBuilder group)
    {
        var queries = group.MapGroup("/queries").WithTags("Queries");

        queries.MapGet("/", (
                [FromQuery] int? queryId,
                [FromQuery] int? dataSourceId,
                [FromQuery] string? queryName,
                [FromQuery] int? folderId,
                [FromQuery] string? searchTerm,
                [FromQuery] int? page,
                [FromQuery] int? pageSize,
                IMediator m,
                CancellationToken ct) =>
        {
            // BaseListRequest.Page is 0-indexed (Skip = Page * PageSize).
            // HTTP query string uses 1-indexed pages for human readability.
            var oneBasedPage = Math.Max(1, page ?? 1);
            return m.Send(new GetQueriesQuery
            {
                Request = new GetQueriesRequest
                {
                    QueryId = queryId,
                    DataSourceId = dataSourceId,
                    QueryName = queryName,
                    FolderId = folderId,
                    SearchTerm = searchTerm,
                    Page = oneBasedPage - 1,
                    PageSize = pageSize ?? 50,
                },
            }, ct);
        }).WithName("GetQueries");

        queries.MapGet("/{id:int}", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetQueryDetailQuery { QueryId = id }, ct))
            .WithName("GetQueryDetail");

        queries.MapPost("/", (CreateQueryBody body, IMediator m, CancellationToken ct) =>
                m.Send(new CreateQueryCommand { Name = body.Name, Description = body.Description }, ct))
            .WithName("CreateQuery");

        queries.MapPost("/{id:int}/lock", (
                int id,
                ToggleQueryLockRequest body,
                IMediator m,
                HttpContext http,
                CancellationToken ct) =>
                m.Send(new ToggleQueryLockCommand
                {
                    QueryId = id,
                    Lock = body.Lock,
                    UserId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                }, ct))
            .WithName("ToggleQueryLock");

        queries.MapGet("/{id:int}/change-history", (
                int id,
                [FromQuery] int? stepId,
                [FromQuery] ChangeSource? changeSource,
                [FromQuery] DateTime? fromDate,
                [FromQuery] DateTime? toDate,
                [FromQuery] int? maxResults,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetQueryChangeHistoryQuery
                {
                    QueryId = id,
                    StepId = stepId,
                    ChangeSource = changeSource,
                    FromDate = fromDate,
                    ToDate = toDate,
                    MaxResults = maxResults ?? 50,
                }, ct))
            .WithName("GetQueryChangeHistory");

        queries.MapPut("/{id:int}", (int id, QueryData body, IMediator m, CancellationToken ct) =>
                m.Send(new UpdateQueryCommand { QueryId = id, Query = body }, ct))
            .WithName("UpdateQuery");

        queries.MapPost("/{id:int}/preview", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ExecuteQueryPreviewCommand { QueryId = id }, ct))
            .WithName("ExecuteQueryPreview");

        queries.MapPost("/{id:int}/steps/{stepOrder:int}/preview", (
                int id,
                int stepOrder,
                ExecuteStepPreviewRequest? body,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new ExecuteStepPreviewCommand
                {
                    QueryId = id,
                    StepOrder = stepOrder,
                    Parameters = body?.Parameters,
                }, ct))
            .WithName("ExecuteStepPreview");

        return group;
    }
}

internal sealed record ToggleQueryLockRequest(bool Lock);
internal sealed record ExecuteStepPreviewRequest(List<ParameterValue>? Parameters);
internal sealed record CreateQueryBody(string Name, string? Description);
