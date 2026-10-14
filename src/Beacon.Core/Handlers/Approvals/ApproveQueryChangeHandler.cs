using MediatR;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class ApproveQueryChangeHandler(
    IQueryApprovalService approvalService,
    ILogger<ApproveQueryChangeHandler> logger,
    IApprovalNotifier? approvalNotifier = null)
    : IRequestHandler<ApproveQueryChangeCommand>
{
    public async Task Handle(ApproveQueryChangeCommand request, CancellationToken cancellationToken)
    {
        // The reviewer is the request's caller, never an id the request carries.
        var decision = await approvalService.ApproveAsync(request.RequestId, request.Comment, request.ExpectedContentHash, cancellationToken);

        // The decision is already persisted above; the push is a side-channel that tells open
        // browser tabs about it. A host that never registered a notifier (Beacon.Api supplies one
        // through AddBeaconApiServices, including when realtime is off) has no one to push to, so
        // the approval stands and we skip the push rather than failing a completed decision.
        if (approvalNotifier == null)
        {
            // Warn, not silence: the decision stood, but nobody was told. Without this line an
            // operator whose UI never live-updates has nothing in the logs to explain why.
            logger.LogWarning(
                "Approval {RequestId} was approved but no IApprovalNotifier is registered, so no client was notified. Call AddBeaconApiServices() to enable push.",
                request.RequestId);
            return;
        }

        // Both are recorded by Users.ExternalId, the id the realtime hub names a signed-in user by.
        await approvalNotifier.ApprovalUpdatedAsync(request.RequestId, "approved", decision.ReviewerUserId, decision.RequesterUserId, cancellationToken);
    }
}

[ProjectScopeExempt("gated by the Approver permission: approving a change is an approval decision, and an Approver reads every approval request", Policy = BeaconPermissions.ApproverPolicy)]
public record ApproveQueryChangeCommand : IRequest
{
    public required int RequestId { get; init; }

    [Obsolete("Ignored: the reviewer is the request's caller.")]
    public string? ReviewerUserId { get; init; }

    [Obsolete("Ignored: the reviewer is named by their user label.")]
    public string? ReviewerName { get; init; }

    public string? Comment { get; init; }

    /// <summary>
    /// The content the reviewer approves: the <c>ContentHash</c> of the request's detail they were shown. Required; an
    /// approval of other content than the proposal holds is refused (409).
    /// </summary>
    public string? ExpectedContentHash { get; init; }
}
