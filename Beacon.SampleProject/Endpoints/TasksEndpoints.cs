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

        return group;
    }
}

internal sealed record ResolveTaskBody(string? ResolutionNotes);
