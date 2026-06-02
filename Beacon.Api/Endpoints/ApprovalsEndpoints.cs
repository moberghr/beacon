using System.Security.Claims;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Models.Queries;
using Beacon.Api.Hubs;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace Beacon.Api.Endpoints;

internal static class ApprovalsEndpoints
{
    public static RouteGroupBuilder MapApprovalsEndpoints(this RouteGroupBuilder group)
    {
        var approvals = group.MapGroup("/approvals").WithTags("Approvals");

        approvals.MapGet("/pending", ([FromQuery] int? queryId, IMediator m, CancellationToken ct) =>
                m.Send(new GetPendingApprovalsQuery { QueryId = queryId }, ct))
            .WithName("GetPendingApprovals");

        approvals.MapGet("/{id:int}", async Task<Results<Ok<ApprovalRequestDetail>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var result = await m.Send(new GetApprovalDetailQuery { RequestId = id }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }).WithName("GetApprovalDetail");

        approvals.MapPost("/{id:int}/approve", async (
            int id,
            ApproveRejectRequest body,
            IMediator m,
            IHubContext<BeaconHub> hub,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (userId, userName) = ResolveActor(http);
            await m.Send(new ApproveQueryChangeCommand
            {
                RequestId = id,
                ReviewerUserId = userId,
                ReviewerName = userName,
                Comment = body.Comment,
            }, ct);
            var detail = await m.Send(new GetApprovalDetailQuery { RequestId = id }, ct);
            await PublishApprovalUpdated(hub, userId, detail?.RequestedByUserId, id, "approved", ct);
            return TypedResults.NoContent();
        }).WithName("ApproveQueryChange");

        approvals.MapPost("/{id:int}/reject", async (
            int id,
            ApproveRejectRequest body,
            IMediator m,
            IHubContext<BeaconHub> hub,
            HttpContext http,
            CancellationToken ct) =>
        {
            var (userId, userName) = ResolveActor(http);
            await m.Send(new RejectQueryChangeCommand
            {
                RequestId = id,
                ReviewerUserId = userId,
                ReviewerName = userName,
                Comment = body.Comment,
            }, ct);
            var detail = await m.Send(new GetApprovalDetailQuery { RequestId = id }, ct);
            await PublishApprovalUpdated(hub, userId, detail?.RequestedByUserId, id, "rejected", ct);
            return TypedResults.NoContent();
        }).WithName("RejectQueryChange");

        return group;
    }

    private static (string? UserId, string? UserName) ResolveActor(HttpContext context)
    {
        var userId = context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var userName = context.User.FindFirst(ClaimTypes.Name)?.Value
            ?? context.User.FindFirst("preferred_username")?.Value
            ?? context.User.Identity?.Name;
        return (userId, userName);
    }

    private static async Task PublishApprovalUpdated(
        IHubContext<BeaconHub> hub,
        string? reviewerUserId,
        string? requesterUserId,
        int approvalId,
        string status,
        CancellationToken ct)
    {
        // Targeted push: notify the reviewer (so other tabs they have open update)
        // and the requester (so they see the approval/rejection live). All other
        // connected clients are intentionally NOT notified — broadcast was overkill.
        var payload = new ApprovalUpdatedEvent(approvalId, status);
        var recipients = new List<string>(2);
        if (!string.IsNullOrEmpty(reviewerUserId))
        {
            recipients.Add(reviewerUserId);
        }

        if (!string.IsNullOrEmpty(requesterUserId) && requesterUserId != reviewerUserId)
        {
            recipients.Add(requesterUserId);
        }

        if (recipients.Count == 0)
        {
            return;
        }

        await hub.Clients.Users(recipients).SendAsync(BeaconHubEventNames.ApprovalUpdated, payload, ct);
    }
}

internal sealed record ApproveRejectRequest(string? Comment);
