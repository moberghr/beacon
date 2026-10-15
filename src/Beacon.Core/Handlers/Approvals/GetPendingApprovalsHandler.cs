using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetPendingApprovalsHandler(
    IQueryApprovalService approvalService,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor)
    : IRequestHandler<GetPendingApprovalsQuery, PagedList<ApprovalRequestSummary>>
{
    public async Task<PagedList<ApprovalRequestSummary>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken)
    {
        // An Approver reviews every project's approval requests; anyone else those of the projects it reads.
        var readable = (await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.Approver)
            ? ProjectSet.All
            : (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);

        return await approvalService.GetPendingApprovalsAsync(request, request.QueryId, readable, cancellationToken);
    }
}

/// <summary>
/// Pending approval requests, newest first unless <c>sort</c> says otherwise: those of the projects the caller reads, and
/// every project's for an Approver.
/// </summary>
public record GetPendingApprovalsQuery : ListRequest, IRequest<PagedList<ApprovalRequestSummary>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? QueryId { get; init; }
}
