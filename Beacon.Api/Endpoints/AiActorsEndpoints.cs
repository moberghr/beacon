using System.Security.Claims;
using Beacon.Core.Handlers.Ai.AiActor;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class AiActorsEndpoints
{
    public static RouteGroupBuilder MapAiActorsEndpoints(this RouteGroupBuilder group)
    {
        var actors = group.MapGroup("/ai-actors").WithTags("AiActors");

        actors.MapGet("/", (
                [FromQuery] int? dataSourceId,
                [FromQuery] bool? includeArchived,
                IMediator m,
                CancellationToken ct) =>
                m.Send(new GetAiActorListQuery
                {
                    DataSourceId = dataSourceId,
                    IncludeArchived = includeArchived,
                }, ct))
            .WithName("GetAiActorList");

        actors.MapPost("/", (CreateAiActorCommand cmd, IMediator m, CancellationToken ct) =>
                m.Send(cmd, ct))
            .WithName("CreateAiActor");

        actors.MapGet("/{id:int}", async Task<Results<Ok<GetAiActorDetailsResult>, NotFound>> (
                int id,
                [FromQuery] int? executionHistoryLimit,
                IMediator m,
                CancellationToken ct) =>
            {
                var result = await m.Send(new GetAiActorDetailsQuery
                {
                    ActorId = id,
                    ExecutionHistoryLimit = executionHistoryLimit ?? 10,
                }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }).WithName("GetAiActorDetails");

        actors.MapDelete("/{id:int}", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ArchiveAiActorCommand { ActorId = id }, ct))
            .WithName("ArchiveAiActor");

        actors.MapPost("/{id:int}/think", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ExecuteAiActorThinkCycleCommand { ActorId = id }, ct))
            .WithName("ExecuteAiActorThinkCycle");

        actors.MapPost("/{id:int}/pause", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new PauseAiActorCommand { ActorId = id }, ct))
            .WithName("PauseAiActor");

        actors.MapPost("/{id:int}/resume", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ResumeAiActorCommand { ActorId = id }, ct))
            .WithName("ResumeAiActor");

        actors.MapPost("/{id:int}/refine", (int id, RefineAiActorBody body, IMediator m, CancellationToken ct) =>
                m.Send(new RefineAiActorCommand
                {
                    ActorId = id,
                    Feedback = body.Feedback,
                }, ct))
            .WithName("RefineAiActor");

        actors.MapGet("/{id:int}/pending-plans", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetPendingPlansQuery { ActorId = id }, ct))
            .WithName("GetPendingPlans");

        actors.MapGet("/plans/{id:int}", async Task<Results<Ok<GetAiActorPlanResult>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var result = await m.Send(new GetAiActorPlanQuery { PlanId = id }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }).WithName("GetAiActorPlan");

        actors.MapPost("/plans/{id:int}/approve", (int id, AiActorPlanDecisionBody body, IMediator m, HttpContext http, CancellationToken ct) =>
                m.Send(new ApproveAiActorPlanCommand
                {
                    PlanId = id,
                    UserId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Comment = body.Comment,
                }, ct))
            .WithName("ApproveAiActorPlan");

        actors.MapPost("/plans/{id:int}/reject", (int id, AiActorPlanRejectBody body, IMediator m, HttpContext http, CancellationToken ct) =>
                m.Send(new RejectAiActorPlanCommand
                {
                    PlanId = id,
                    UserId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Reason = body.Reason,
                }, ct))
            .WithName("RejectAiActorPlan");

        actors.MapPost("/plans/{id:int}/request-revision", (int id, AiActorPlanRevisionBody body, IMediator m, HttpContext http, CancellationToken ct) =>
                m.Send(new RequestPlanRevisionCommand
                {
                    PlanId = id,
                    UserId = http.User.FindFirst(ClaimTypes.NameIdentifier)?.Value,
                    Feedback = body.Feedback,
                }, ct))
            .WithName("RequestPlanRevision");

        return group;
    }
}

internal sealed record RefineAiActorBody(string Feedback);
internal sealed record AiActorPlanDecisionBody(string? Comment);
internal sealed record AiActorPlanRejectBody(string Reason);
internal sealed record AiActorPlanRevisionBody(string Feedback);
