using System.Security.Claims;
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

        return group;
    }
}

internal sealed record ResolveTaskBody(string? ResolutionNotes);
internal sealed record AddTaskCommentBody(string Content);
