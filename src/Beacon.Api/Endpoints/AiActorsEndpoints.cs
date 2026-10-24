using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Handlers.AiActors;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class AiActorsEndpoints
{
    public static RouteGroupBuilder MapAiActorsEndpoints(this RouteGroupBuilder group)
    {
        var actors = group.MapGroup("/ai-actors").WithTags("AiActors");

        actors.MapGet("/", ([AsParameters] GetAiActorListQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
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

        // Runs the cycle in the request and answers with its result (kept for 5.0 callers; a cycle holds the request open).
        actors.MapPost("/{id:int}/think", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new ExecuteAiActorThinkCycleCommand { ActorId = id }, ct))
            .WithName("ExecuteAiActorThinkCycle");

        // Starts the cycle as a background task: 202 with the task (its job id correlates JobStatusChanged), read below.
        actors.MapPost("/{id:int}/think-tasks", async (int id, IMediator m, CancellationToken ct) =>
            {
                var ticket = await m.Send(new StartAiActorThinkCommand { ActorId = id }, ct);
                return TypedResults.Accepted($"/beacon/api/ai-actors/{id}/think-tasks/{ticket.TaskId}", ticket);
            })
            .WithName("StartAiActorThink");

        actors.MapGet("/{id:int}/think-tasks/{taskId:int}", async Task<Results<Ok<BackgroundTaskStatus>, NotFound>> (int id, int taskId, IMediator m, CancellationToken ct) =>
            {
                var status = await m.Send(new GetAiActorThinkTaskQuery(id, taskId), ct);
                return status is null ? TypedResults.NotFound() : TypedResults.Ok(status);
            })
            .WithName("GetAiActorThinkTask");

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

        // Making a user the creator of an actor (for instance one created before creators were recorded) is a UserAdmin's.
        actors.MapPut("/{id:int}/owner", async (int id, SetAiActorOwnerBody body, IMediator m, CancellationToken ct) =>
            {
                await m.Send(new SetAiActorOwnerCommand(id, body.UserId), ct);
                return TypedResults.NoContent();
            })
            .WithName("SetAiActorOwner")
            .RequireBeaconPermission(BeaconPermissions.UserAdminPolicy);

        actors.MapGet("/{id:int}/pending-plans", (int id, IMediator m, CancellationToken ct) =>
                m.Send(new GetPendingPlansQuery { ActorId = id }, ct))
            .WithName("GetPendingPlans");

        actors.MapGet("/pending-plans/count", (IMediator m, CancellationToken ct) =>
                m.Send(new GetPendingPlanCountQuery(), ct))
            .WithName("GetPendingPlanCount");

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
internal sealed record SetAiActorOwnerBody(int UserId);
