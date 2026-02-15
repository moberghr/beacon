using MediatR;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Approvals;

internal sealed class RejectQueryChangeHandler(IQueryApprovalService approvalService)
    : IRequestHandler<RejectQueryChangeCommand>
{
    public async Task Handle(RejectQueryChangeCommand request, CancellationToken cancellationToken)
    {
        await approvalService.RejectAsync(request.RequestId, request.ReviewerUserId, request.ReviewerName, request.Comment, cancellationToken);
    }
}

public record RejectQueryChangeCommand : IRequest
{
    public required int RequestId { get; init; }
    public string? ReviewerUserId { get; init; }
    public string? ReviewerName { get; init; }
    public string? Comment { get; init; }
}
