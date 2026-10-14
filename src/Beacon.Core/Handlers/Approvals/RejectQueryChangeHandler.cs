using MediatR;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class RejectQueryChangeHandler(
    IQueryApprovalService approvalService,
    ILogger<RejectQueryChangeHandler> logger,
    IApprovalNotifier? approvalNotifier = null)
    : IRequestHandler<RejectQueryChangeCommand>
{
    public async Task Handle(RejectQueryChangeCommand request, CancellationToken cancellationToken)
    {
        // The reviewer is the request's caller, never an id the request carries.
        var decision = await approvalService.RejectAsync(request.RequestId, request.Comment, cancellationToken);

        // The decision is already persisted above; the push is a side-channel that tells open
        // browser tabs about it. A host that never registered a notifier (Beacon.Api supplies one
        // through AddBeaconApiServices, including when realtime is off) has no one to push to, so
        // the rejection stands and we skip the push rather than failing a completed decision.
        if (approvalNotifier == null)
        {
            // Warn, not silence: the decision stood, but nobody was told. Without this line an
            // operator whose UI never live-updates has nothing in the logs to explain why.
            logger.LogWarning(
                "Approval {RequestId} was rejected but no IApprovalNotifier is registered, so no client was notified. Call AddBeaconApiServices() to enable push.",
                request.RequestId);
            return;
        }

        // Both are recorded by Users.ExternalId, the id the realtime hub names a signed-in user by.
        await approvalNotifier.ApprovalUpdatedAsync(request.RequestId, "rejected", decision.ReviewerUserId, decision.RequesterUserId, cancellationToken);
    }
}

[ProjectScopeExempt("gated by the Approver permission: rejecting a change is an approval decision, and an Approver reads every approval request", Policy = BeaconPermissions.ApproverPolicy)]
public record RejectQueryChangeCommand : IRequest
{
    public required int RequestId { get; init; }

    [Obsolete("Ignored: the reviewer is the request's caller.")]
    public string? ReviewerUserId { get; init; }

    [Obsolete("Ignored: the reviewer is named by their user label.")]
    public string? ReviewerName { get; init; }

    public string? Comment { get; init; }
}
