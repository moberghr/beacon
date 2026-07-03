using MediatR;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class RejectQueryChangeHandler(
    IQueryApprovalService approvalService,
    IApprovalNotifier approvalNotifier)
    : IRequestHandler<RejectQueryChangeCommand>
{
    public async Task Handle(RejectQueryChangeCommand request, CancellationToken cancellationToken)
    {
        await approvalService.RejectAsync(request.RequestId, request.ReviewerUserId, request.ReviewerName, request.Comment, cancellationToken);

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
