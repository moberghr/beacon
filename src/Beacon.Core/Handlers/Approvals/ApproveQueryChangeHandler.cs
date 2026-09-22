using MediatR;
using Beacon.Core.Services;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class ApproveQueryChangeHandler(
    IQueryApprovalService approvalService,
    ILogger<ApproveQueryChangeHandler> logger,
    IApprovalNotifier? approvalNotifier = null)
    : IRequestHandler<ApproveQueryChangeCommand>
{
    public async Task Handle(ApproveQueryChangeCommand request, CancellationToken cancellationToken)
    {
        await approvalService.ApproveAsync(request.RequestId, request.ReviewerUserId, request.ReviewerName, request.Comment, cancellationToken);

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

        var detail = await approvalService.GetApprovalDetailAsync(request.RequestId, cancellationToken);
        await approvalNotifier.ApprovalUpdatedAsync(request.RequestId, "approved", request.ReviewerUserId, detail?.RequestedByUserId, cancellationToken);
    }
}

public record ApproveQueryChangeCommand : IRequest
{
    public required int RequestId { get; init; }
    public string? ReviewerUserId { get; init; }
    public string? ReviewerName { get; init; }
    public string? Comment { get; init; }
}
