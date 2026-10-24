using Beacon.Api.Authentication;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Approvals;
using Beacon.Core.Models.Approvals;
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

        // Changes of migration jobs, data contracts' Custom SQL rules, recipients, data-source connections and the LLM
        // connection: an Approver's to decide, the LLM connection's a live Admin's. An Approver reads every one but the LLM
        // connection's, which only a live Admin reads; anyone else those of the migration jobs and data contracts it reads.
        approvals.MapGet("/changes", ([AsParameters] GetChangeApprovalsQuery query, IMediator m, CancellationToken ct) => m.Send(query, ct))
            .WithName("GetChangeApprovals")
            .AdmitBeaconPermission(BeaconPermissions.Approver);

        approvals.MapGet("/changes/{id:int}", async Task<Results<Ok<ChangeApprovalDetail>, NotFound>> (int id, IMediator m, CancellationToken ct) =>
            {
                var result = await m.Send(new GetChangeApprovalDetailQuery { RequestId = id }, ct);
                return result is null ? TypedResults.NotFound() : TypedResults.Ok(result);
            }).WithName("GetChangeApprovalDetail")
            .AdmitBeaconPermission(BeaconPermissions.Approver);

        approvals.MapPost("/changes/{id:int}/approve", async (int id, ChangeDecisionRequest body, IMediator m, CancellationToken ct) =>
        {
            // The reviewer is the request's caller, resolved by the service — never an id the request carries.
            await m.Send(new ApproveChangeCommand
            {
                RequestId = id,
                Comment = body.Comment,
                ExpectedPayloadHash = body.ExpectedPayloadHash,
            }, ct);
            return TypedResults.NoContent();
        }).WithName("ApproveChange")
            .RequireBeaconPermission(BeaconPermissions.ApproverPolicy);

        approvals.MapPost("/changes/{id:int}/reject", async (int id, ChangeDecisionRequest body, IMediator m, CancellationToken ct) =>
        {
            await m.Send(new RejectChangeCommand
            {
                RequestId = id,
                Comment = body.Comment,
            }, ct);
            return TypedResults.NoContent();
        }).WithName("RejectChange")
            .RequireBeaconPermission(BeaconPermissions.ApproverPolicy);

        return group;
    }
}

/// <param name="Comment">The reviewer's note, shown with the decision.</param>
/// <param name="ExpectedPayloadHash">Approving: the <c>payloadHash</c> of the request's detail the reviewer was shown
/// (required; 409 when the request holds another payload). Ignored when rejecting.</param>
internal sealed record ChangeDecisionRequest(string? Comment, string? ExpectedPayloadHash = null);

/// <param name="Comment">The reviewer's note, shown with the decision.</param>
/// <param name="ExpectedContentHash">Approving: the <c>contentHash</c> of the request's detail the reviewer was shown
/// (required; 409 when the proposal holds other content). Ignored when rejecting.</param>
internal sealed record ApproveRejectRequest(string? Comment, string? ExpectedContentHash = null);
