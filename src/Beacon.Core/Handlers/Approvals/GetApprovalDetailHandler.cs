using MediatR;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Approvals;

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
