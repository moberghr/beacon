using System.Security.Claims;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Tasks;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class TasksEndpoints
{
    public static RouteGroupBuilder MapTasksEndpoints(this RouteGroupBuilder group)
    {
        var tasks = group.MapGroup("/tasks").WithTags("Tasks");

        tasks.MapGet("/", (
                IMediator m,
                CancellationToken ct,
                [FromQuery] int? subscriptionId,
                [FromQuery] bool? resolved,
                [FromQuery] string? sortColumn,
                [FromQuery] bool sortDescending = false,
                [FromQuery] int page = 0,
                [FromQuery] int pageSize = 25) =>
                m.Send(new GetTasksQuery(
                    Page: page,
                    PageSize: pageSize <= 0 ? 25 : pageSize,
                    SubscriptionId: subscriptionId,
                    Resolved: resolved,
                    SortColumn: sortColumn,
                    SortDescending: sortDescending), ct))
            .WithName("GetTasks");

        tasks.MapGet("/{id:int}", async Task<Results<Ok<TaskDetailResult>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
        {
            var result = await m.Send(new GetTaskDetailQuery(id), ct);
            return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
        }).WithName("GetTaskDetail");

        tasks.MapPost("/{id:int}/resolve", async (
            int id, ResolveTaskBody body, IMediator m, HttpContext http, CancellationToken ct) =>
        {
            var userId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            await m.Send(new ResolveTaskCommand(id, body.ResolutionNotes, userId), ct);
            return TypedResults.NoContent();
        }).WithName("ResolveTask");

        tasks.MapGet("/{id:int}/executions", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetTaskExecutionsQuery(id), ct))
            .WithName("GetTaskExecutions");

        tasks.MapGet("/{id:int}/related", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetTaskRelatedQuery(id), ct))
            .WithName("GetTaskRelated");

        tasks.MapGet("/{id:int}/result-history", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetTaskResultHistoryQuery(id), ct))
            .WithName("GetTaskResultHistory");

        tasks.MapGet("/{id:int}/comments", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetTaskCommentsQuery(id), ct))
            .WithName("GetTaskComments");

        tasks.MapPost("/{id:int}/comments", (int id, [FromBody] AddTaskCommentBody body, IMediator m, CancellationToken ct) =>
                m.Send(new AddTaskCommentCommand(id, body.Content), ct))
            .WithName("AddTaskComment");

        tasks.MapPost("/{id:int}/assign", async (int id, [FromBody] AssignTaskBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new AssignTaskCommand(id, body.AssigneeUserId), ct);
            return TypedResults.NoContent();
        }).WithName("AssignTask");

        tasks.MapPost("/{id:int}/snooze", async (int id, [FromBody] SnoozeTaskBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SnoozeTaskCommand(id, body.SnoozeUntil), ct);
            return TypedResults.NoContent();
        }).WithName("SnoozeTask");

        tasks.MapPost("/{id:int}/priority", async (int id, [FromBody] SetTaskPriorityBody body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new SetTaskPriorityCommand(id, body.Priority), ct);
            return TypedResults.NoContent();
        }).WithName("SetTaskPriority");

        tasks.MapPost("/{id:int}/watch", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new WatchTaskCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("WatchTask");

        tasks.MapPost("/{id:int}/unwatch", async (int id, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new UnwatchTaskCommand(id), ct);
            return TypedResults.NoContent();
        }).WithName("UnwatchTask");

        return group;
    }
}

internal sealed record ResolveTaskBody(string? ResolutionNotes);
internal sealed record AddTaskCommentBody(string Content);
internal sealed record AssignTaskBody(string? AssigneeUserId);
internal sealed record SnoozeTaskBody(DateTime? SnoozeUntil);
internal sealed record SetTaskPriorityBody(TaskPriority Priority);
