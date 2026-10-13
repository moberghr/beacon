using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Models.Queries;
using MediatR;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Beacon.Api.Endpoints;

internal static class ApprovalsEndpoints
{
    public static RouteGroupBuilder MapApprovalsEndpoints(this RouteGroupBuilder group)
    {
        var approvals = group.MapGroup("/approvals").WithTags("Approvals");

        approvals.MapGet("/pending", ([AsParameters] GetPendingApprovalsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetPendingApprovals")
            .AdmitBeaconPermission(BeaconPermissions.Approver);

        approvals.MapGet("/{id:int}", async Task<Results<Ok<ApprovalRequestDetail>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var result = await m.Send(new GetApprovalDetailQuery { RequestId = id }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }).WithName("GetApprovalDetail")
            .AdmitBeaconPermission(BeaconPermissions.Approver);

        approvals.MapPost("/{id:int}/approve", async (
            int id,
            ApproveRejectRequest body,
            IMediator m,
            CancellationToken ct) =>
        {
            // The reviewer is the request's caller, resolved by the service — never an id the request carries.
            await m.Send(new ApproveQueryChangeCommand
            {
                RequestId = id,
                Comment = body.Comment,
                ExpectedContentHash = body.ExpectedContentHash,
            }, ct);
            return TypedResults.NoContent();
        }).WithName("ApproveQueryChange")
            .RequireBeaconPermission(BeaconPermissions.ApproverPolicy);

        approvals.MapPost("/{id:int}/reject", async (
            int id,
            ApproveRejectRequest body,
            IMediator m,
            CancellationToken ct) =>
        {
            // The reviewer is the request's caller, resolved by the service — never an id the request carries.
            await m.Send(new RejectQueryChangeCommand
            {
                RequestId = id,
                Comment = body.Comment,
            }, ct);
            return TypedResults.NoContent();
        }).WithName("RejectQueryChange")
            .RequireBeaconPermission(BeaconPermissions.ApproverPolicy);

        return group;
    }
}

/// <param name="Comment">The reviewer's note, shown with the decision.</param>
/// <param name="ExpectedContentHash">Approving: the <c>contentHash</c> of the request's detail the reviewer was shown
/// (required; 409 when the proposal holds other content). Ignored when rejecting.</param>
internal sealed record ApproveRejectRequest(string? Comment, string? ExpectedContentHash = null);
