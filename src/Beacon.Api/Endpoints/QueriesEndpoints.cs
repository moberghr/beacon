using Beacon.Core.Helpers;
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

        queries.MapGet("/", ([AsParameters] GetQueriesQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetQueries");

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

        queries.MapGet("/{queryId:int}/change-history", ([AsParameters] GetQueryChangeHistoryQuery query, IMediator m, CancellationToken ct) =>
                m.Send(query, ct))
            .WithName("GetQueryChangeHistory");

        queries.MapPut("/{id:int}", (int id, QueryData body, IMediator m, CancellationToken ct) =>
                m.Send(new UpdateQueryCommand { QueryId = id, Query = body }, ct))
            .WithName("UpdateQuery");

        // Exposes / withdraws the query as the MCP tool q_<name>. Admin-only: it publishes reviewed SQL to every
        // MCP caller of the projects that contain the query's data sources.
        queries.MapPut("/{id:int}/mcp-tool", (
                int id,
                SetQueryMcpToolRequest body,
                IMediator m,
                HttpContext http,
                CancellationToken ct) =>
                m.Send(new SetQueryMcpToolCommand(
                    id,
                    body.Enabled,
                    body.Name,
                    body.Description,
                    http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value), ct))
            .WithName("SetQueryMcpTool")
            .RequireAuthorization(BeaconApiEndpoints.AdminPolicyName);

        // SQL-executing endpoints: require the Execute (or Admin) scope for API-key callers (§1.4).
        // Interactive cookie/OIDC sessions carry no scope claim and pass through, governed by role.
        // An optional draft runs the editor's unsaved steps instead of the stored query, so Run never saves.
        queries.MapPost("/{id:int}/preview", (
                int id,
                ExecuteQueryPreviewRequest? body,
                [AsParameters] PreviewPageQuery paging,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new ExecuteQueryPreviewCommand
                {
                    QueryId = id,
                    Draft = body?.Draft,
                    Page = paging.Page,
                    PageSize = paging.PageSize,
                    Sort = paging.Sort,
                }, ct))
            .WithName("ExecuteQueryPreview")
            .RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        queries.MapPost("/{id:int}/steps/{stepOrder:int}/preview", (
                int id,
                int stepOrder,
                ExecuteStepPreviewRequest? body,
                [AsParameters] PreviewPageQuery paging,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new ExecuteStepPreviewCommand
                {
                    QueryId = id,
                    StepOrder = stepOrder,
                    Parameters = body?.Parameters,
                    Draft = body?.Draft,
                    Page = paging.Page,
                    PageSize = paging.PageSize,
                    Sort = paging.Sort,
                }, ct))
            .WithName("ExecuteStepPreview")
            .RequireAuthorization(BeaconApiEndpoints.ExecuteScopePolicyName);

        return group;
    }
}

internal sealed record ToggleQueryLockRequest(bool Lock);
internal sealed record ExecuteQueryPreviewRequest(QueryDraft? Draft);
internal sealed record ExecuteStepPreviewRequest(List<ParameterValue>? Parameters, QueryDraft? Draft);
internal sealed record CreateQueryBody(string Name, string? Description);
internal sealed record SetQueryMcpToolRequest(bool Enabled, string? Name, string? Description);

/// <summary>`?page=&amp;pageSize=&amp;sort=` for the preview endpoints, whose route and body carry the rest.</summary>
internal sealed record PreviewPageQuery : ListRequest;
