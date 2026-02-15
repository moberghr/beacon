using MediatR;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Approvals;

internal sealed class GetPendingApprovalCountHandler(IQueryApprovalService approvalService)
    : IRequestHandler<GetPendingApprovalCountQuery, int>
{
    public async Task<int> Handle(GetPendingApprovalCountQuery request, CancellationToken cancellationToken)
    {
        return await approvalService.GetPendingCountAsync(cancellationToken);
    }
}

public record GetPendingApprovalCountQuery : IRequest<int>;
