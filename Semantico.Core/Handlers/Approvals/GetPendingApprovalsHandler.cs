using MediatR;
using Semantico.Core.Models.Queries;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Approvals;

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
