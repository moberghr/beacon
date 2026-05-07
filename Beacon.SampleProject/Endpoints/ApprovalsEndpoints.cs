using System.Security.Claims;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Models.Queries;
using Beacon.SampleProject.Hubs;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.SampleProject.Endpoints;

internal static class ApprovalsEndpoints
{
    public static RouteGroupBuilder MapApprovalsEndpoints(this RouteGroupBuilder group)
    {
        var approvals = group.MapGroup("/approvals").WithTags("Approvals");

        approvals.MapGet("/pending", async (
                [FromQuery] int? queryId,
                IMediator mediator,
                CancellationToken ct) =>
                Results.Ok(await mediator.Send(new GetPendingApprovalsQuery { QueryId = queryId }, ct)))
            .WithName("GetPendingApprovals")
            .Produces<List<ApprovalRequestSummary>>(StatusCodes.Status200OK);

        approvals.MapGet("/{id:int}", async (int id, IMediator mediator, CancellationToken ct) =>
            {
                var result = await mediator.Send(new GetApprovalDetailQuery { RequestId = id }, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            })
            .WithName("GetApprovalDetail")
            .Produces<ApprovalRequestDetail>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);

        approvals.MapPost("/{id:int}/approve", async (
                int id,
                ApproveRejectRequest body,
                IMediator mediator,
                IHubContext<BeaconHub> hub,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                await mediator.Send(new ApproveQueryChangeCommand
                {
                    RequestId = id,
                    ReviewerUserId = body.ReviewerUserId,
                    ReviewerName = body.ReviewerName,
                    Comment = body.Comment,
                }, ct);

                await PublishApprovalUpdated(hub, httpContext, id, "approved", ct);
                return Results.NoContent();
            })
            .WithName("ApproveQueryChange")
            .Produces(StatusCodes.Status204NoContent);

        approvals.MapPost("/{id:int}/reject", async (
                int id,
                ApproveRejectRequest body,
                IMediator mediator,
                IHubContext<BeaconHub> hub,
                HttpContext httpContext,
                CancellationToken ct) =>
            {
                await mediator.Send(new RejectQueryChangeCommand
                {
                    RequestId = id,
                    ReviewerUserId = body.ReviewerUserId,
                    ReviewerName = body.ReviewerName,
                    Comment = body.Comment,
                }, ct);

                await PublishApprovalUpdated(hub, httpContext, id, "rejected", ct);
                return Results.NoContent();
            })
            .WithName("RejectQueryChange")
            .Produces(StatusCodes.Status204NoContent);

        return group;
    }

    private static async Task PublishApprovalUpdated(
        IHubContext<BeaconHub> hub,
        HttpContext context,
        int approvalId,
        string status,
        CancellationToken ct)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(userId))
        {
            return;
        }

        var payload = new ApprovalUpdatedEvent(approvalId, status);
        await hub.Clients.User(userId).SendAsync(BeaconHubEventNames.ApprovalUpdated, payload, ct);
    }
}

internal sealed record ApproveRejectRequest(string? ReviewerUserId, string? ReviewerName, string? Comment);
