using MediatR;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetPendingApprovalsHandler(IQueryApprovalService approvalService)
    : IRequestHandler<GetPendingApprovalsQuery, List<ApprovalRequestSummary>>
{
    public async Task<List<ApprovalRequestSummary>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken)
    {
        return await approvalService.GetPendingApprovalsAsync(request.QueryId, cancellationToken);
    }
}

public record GetPendingApprovalsQuery : IRequest<List<ApprovalRequestSummary>>
{
    public int? QueryId { get; init; }
}
