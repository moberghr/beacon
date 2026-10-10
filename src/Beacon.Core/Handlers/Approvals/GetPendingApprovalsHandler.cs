using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetPendingApprovalsHandler(IQueryApprovalService approvalService, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetPendingApprovalsQuery, PagedList<ApprovalRequestSummary>>
{
    public async Task<PagedList<ApprovalRequestSummary>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        return await approvalService.GetPendingApprovalsAsync(request, request.QueryId, scope.For(ProjectAction.Read), cancellationToken);
    }
}

/// <summary>Pending approval requests, newest first unless <c>sort</c> says otherwise.</summary>
public record GetPendingApprovalsQuery : ListRequest, IRequest<PagedList<ApprovalRequestSummary>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? QueryId { get; init; }
}
