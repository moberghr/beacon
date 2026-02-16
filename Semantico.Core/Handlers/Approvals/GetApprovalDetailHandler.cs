using MediatR;
using Semantico.Core.Models.Queries;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Approvals;

internal sealed class GetApprovalDetailHandler(IQueryApprovalService approvalService)
    : IRequestHandler<GetApprovalDetailQuery, ApprovalRequestDetail?>
{
    public async Task<ApprovalRequestDetail?> Handle(GetApprovalDetailQuery request, CancellationToken cancellationToken)
    {
        return await approvalService.GetApprovalDetailAsync(request.RequestId, cancellationToken);
    }
}

public record GetApprovalDetailQuery : IRequest<ApprovalRequestDetail?>
{
    public required int RequestId { get; init; }
}
