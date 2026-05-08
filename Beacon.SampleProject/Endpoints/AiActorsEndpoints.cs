using System.Security.Claims;
using Beacon.Core.Handlers.Ai.AiActor;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.SampleProject.Endpoints;

internal static class AiActorsEndpoints
{
    public static RouteGroupBuilder MapAiActorsEndpoints(this RouteGroupBuilder group)
    {
        var actors = group.MapGroup("/ai-actors").WithTags("AiActors");

        actors.MapGet("/", async (
                [FromQuery] int dataSourceId,
                [FromQuery] bool? includeArchived,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetAiActorListQuery
                {
                    DataSourceId = dataSourceId,
                    IncludeArchived = includeArchived,
                }, ct)))
            .WithName("GetAiActorList")
            .Produces<GetAiActorListResult>(StatusCodes.Status200OK);

        actors.MapPost("/", async (CreateAiActorCommand command, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateAiActor")
            .Produces<CreateAiActorResult>(StatusCodes.Status200OK);

        actors.MapGet("/{id:int}", async (
                int id,
                [FromQuery] int? executionHistoryLimit,
                IMediator mediator,
                CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAiActorDetailsQuery
                {
                    ActorId = id,
                    ExecutionHistoryLimit = executionHistoryLimit ?? 10,
                }, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetAiActorDetails")
            .Produces<GetAiActorDetailsResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        actors.MapDelete("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ArchiveAiActorCommand { ActorId = id }, ct)))
            .WithName("ArchiveAiActor")
            .Produces<ArchiveAiActorResult>(StatusCodes.Status200OK);

        actors.MapPost("/{id:int}/think", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ExecuteAiActorThinkCycleCommand { ActorId = id }, ct)))
            .WithName("ExecuteAiActorThinkCycle")
            .Produces<ExecuteAiActorThinkCycleResult>(StatusCodes.Status200OK);

        actors.MapPost("/{id:int}/pause", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new PauseAiActorCommand { ActorId = id }, ct)))
            .WithName("PauseAiActor")
            .Produces<PauseAiActorResult>(StatusCodes.Status200OK);

        actors.MapPost("/{id:int}/resume", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ResumeAiActorCommand { ActorId = id }, ct)))
            .WithName("ResumeAiActor")
            .Produces<ResumeAiActorResult>(StatusCodes.Status200OK);

        actors.MapPost("/{id:int}/refine", async (int id, RefineAiActorBody body, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new RefineAiActorCommand
                {
                    ActorId = id,
                    Feedback = body.Feedback,
                }, ct)))
            .WithName("RefineAiActor")
            .Produces<RefineAiActorResult>(StatusCodes.Status200OK);

        actors.MapGet("/{id:int}/pending-plans", async (int id, IMediator mediator, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetPendingPlansQuery { ActorId = id }, ct)))
            .WithName("GetPendingPlans")
            .Produces<GetPendingPlansResult>(StatusCodes.Status200OK);

        actors.MapGet("/plans/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetAiActorPlanQuery { PlanId = id }, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetAiActorPlan")
            .Produces<GetAiActorPlanResult>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        actors.MapPost("/plans/{id:int}/approve", async (int id, AiActorPlanDecisionBody body, IMediator mediator, HttpContext httpContext, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new ApproveAiActorPlanCommand
                {
                    PlanId = id,
                    UserId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Comment = body.Comment,
                }, ct)))
            .WithName("ApproveAiActorPlan")
            .Produces<ApproveAiActorPlanResult>(StatusCodes.Status200OK);

        actors.MapPost("/plans/{id:int}/reject", async (int id, AiActorPlanRejectBody body, IMediator mediator, HttpContext httpContext, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new RejectAiActorPlanCommand
                {
                    PlanId = id,
                    UserId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Reason = body.Reason,
                }, ct)))
            .WithName("RejectAiActorPlan")
            .Produces<RejectAiActorPlanResult>(StatusCodes.Status200OK);

        actors.MapPost("/plans/{id:int}/request-revision", async (int id, AiActorPlanRevisionBody body, IMediator mediator, HttpContext httpContext, CancellationToken ct) =>
                Results.Ok(await mediator.Send(new RequestPlanRevisionCommand
                {
                    PlanId = id,
                    UserId = httpContext.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Feedback = body.Feedback,
                }, ct)))
            .WithName("RequestPlanRevision")
            .Produces<RequestPlanRevisionResult>(StatusCodes.Status200OK);

        return group;
    }
}

internal sealed record RefineAiActorBody(string Feedback);
internal sealed record AiActorPlanDecisionBody(string? Comment);
internal sealed record AiActorPlanRejectBody(string Reason);
internal sealed record AiActorPlanRevisionBody(string Feedback);
