using System.Security.Claims;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class TasksEndpoints
{
    public static RouteGroupBuilder MapTasksEndpoints(this RouteGroupBuilder group)
    {
        var tasks = group.MapGroup("/tasks").WithTags("Tasks");

        tasks.MapGet("/", async (
                [FromQuery] int page,
                [FromQuery] int pageSize,
                [FromQuery] int? subscriptionId,
                [FromQuery] bool? resolved,
                [FromQuery] string? sortColumn,
                [FromQuery] bool sortDescending,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(
                    new GetTasksQuery(
                        Page: page,
                        PageSize: pageSize <= 0 ? 25 : pageSize,
                        SubscriptionId: subscriptionId,
                        Resolved: resolved,
                        SortColumn: sortColumn,
                        SortDescending: sortDescending),
                    ct)))
            .WithName("GetTasks")
            .Produces<GetTasksResult>(StatusCodes.Status200OK);

        tasks.MapGet("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetTaskDetailQuery(id), ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetTaskDetail")
            .Produces<TaskDetailResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        tasks.MapPost("/{id:int}/resolve", async (
                int id,
                ResolveTaskBody body,
                IMediator mediator,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                var userId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                await mediator.Send(new ResolveTaskCommand(id, body.ResolutionNotes, userId), ct);
                return Results.NoContent();
            })
            .WithName("ResolveTask")
            .Produces(StatusCodes.Status204NoContent);

        tasks.MapGet("/{id:int}/executions", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetTaskExecutionsQuery(id), ct)))
            .WithName("GetTaskExecutions")
            .Produces<TaskExecutionsResult>(StatusCodes.Status200OK);

        tasks.MapGet("/{id:int}/related", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetTaskRelatedQuery(id), ct)))
            .WithName("GetTaskRelated")
            .Produces<TaskRelatedResult>(StatusCodes.Status200OK);

        tasks.MapGet("/{id:int}/result-history", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetTaskResultHistoryQuery(id), ct)))
            .WithName("GetTaskResultHistory")
            .Produces<TaskResultHistoryResult>(StatusCodes.Status200OK);

        tasks.MapGet("/{id:int}/comments", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetTaskCommentsQuery(id), ct)))
            .WithName("GetTaskComments")
            .Produces<TaskCommentsResult>(StatusCodes.Status200OK);

        tasks.MapPost("/{id:int}/comments", async (
                int id,
                [FromBody] AddTaskCommentBody body,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new AddTaskCommentCommand(id, body.Content), ct)))
            .WithName("AddTaskComment")
            .Produces<AddTaskCommentResult>(StatusCodes.Status200OK);

        tasks.MapPost("/{id:int}/assign", async (
                int id,
                [FromBody] AssignTaskBody body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new AssignTaskCommand(id, body.AssigneeUserId), ct);
                return Results.NoContent();
            })
            .WithName("AssignTask")
            .Produces(StatusCodes.Status204NoContent);

        tasks.MapPost("/{id:int}/snooze", async (
                int id,
                [FromBody] SnoozeTaskBody body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new SnoozeTaskCommand(id, body.SnoozeUntil), ct);
                return Results.NoContent();
            })
            .WithName("SnoozeTask")
            .Produces(StatusCodes.Status204NoContent);

        tasks.MapPost("/{id:int}/priority", async (
                int id,
                [FromBody] SetTaskPriorityBody body,
                IMediator mediator,
                CancellationToken ct) =>
            {
                await mediator.Send(new SetTaskPriorityCommand(id, body.Priority), ct);
                return Results.NoContent();
            })
            .WithName("SetTaskPriority")
            .Produces(StatusCodes.Status204NoContent);

        tasks.MapPost("/{id:int}/watch", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new WatchTaskCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("WatchTask")
            .Produces(StatusCodes.Status204NoContent);

        tasks.MapPost("/{id:int}/unwatch", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                await mediator.Send(new UnwatchTaskCommand(id), ct);
                return Results.NoContent();
            })
            .WithName("UnwatchTask")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }
}

internal sealed record ResolveTaskBody(string? ResolutionNotes);
internal sealed record AddTaskCommentBody(string Content);
internal sealed record AssignTaskBody(string? AssigneeUserId);
internal sealed record SnoozeTaskBody(DateTime? SnoozeUntil);
internal sealed record SetTaskPriorityBody(TaskPriority Priority);
