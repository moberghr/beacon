using MediatR;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class RejectQueryChangeHandler(
    IQueryApprovalService approvalService,
    ILogger<RejectQueryChangeHandler> logger,
    IApprovalNotifier? approvalNotifier = null)
    : IRequestHandler<RejectQueryChangeCommand>
{
    public async Task Handle(RejectQueryChangeCommand request, CancellationToken cancellationToken)
    {
        await approvalService.RejectAsync(request.RequestId, request.ReviewerUserId, request.ReviewerName, request.Comment, cancellationToken);

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

        var detail = await approvalService.GetApprovalDetailAsync(request.RequestId, cancellationToken);
        await approvalNotifier.ApprovalUpdatedAsync(request.RequestId, "rejected", request.ReviewerUserId, detail?.RequestedByUserId, cancellationToken);
    }
}

public record RejectQueryChangeCommand : IRequest
{
    public required int RequestId { get; init; }
    public string? ReviewerUserId { get; init; }
    public string? ReviewerName { get; init; }
    public string? Comment { get; init; }
}
