using MediatR;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Approvals;

internal sealed class ApproveQueryChangeHandler(IQueryApprovalService approvalService)
    : IRequestHandler<ApproveQueryChangeCommand>
{
    public async Task Handle(ApproveQueryChangeCommand request, CancellationToken cancellationToken)
    {
        await approvalService.ApproveAsync(request.RequestId, request.ReviewerUserId, request.ReviewerName, request.Comment, cancellationToken);
    }
}

public record ApproveQueryChangeCommand : IRequest
{
    public required int RequestId { get; init; }
    public string? ReviewerUserId { get; init; }
    public string? ReviewerName { get; init; }
    public string? Comment { get; init; }
}
