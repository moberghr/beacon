using MediatR;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class ApproveQueryChangeHandler(
    IQueryApprovalService approvalService,
    IApprovalNotifier approvalNotifier)
    : IRequestHandler<ApproveQueryChangeCommand>
{
    public async Task Handle(ApproveQueryChangeCommand request, CancellationToken cancellationToken)
    {
        await approvalService.ApproveAsync(request.RequestId, request.ReviewerUserId, request.ReviewerName, request.Comment, cancellationToken);

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
