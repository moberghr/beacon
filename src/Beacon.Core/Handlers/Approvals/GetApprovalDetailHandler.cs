using MediatR;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetApprovalDetailHandler(IQueryApprovalService approvalService)
    : IRequestHandler<GetApprovalDetailQuery, ApprovalRequestDetail?>
{
    public async Task<ApprovalRequestDetail?> Handle(GetApprovalDetailQuery request, CancellationToken cancellationToken)
    {
        return await approvalService.GetApprovalDetailAsync(request.RequestId, cancellationToken);
    }
}

/// <summary>An approval request, for a caller who reads its query version, and for an Approver.</summary>
public record GetApprovalDetailQuery : IRequest<ApprovalRequestDetail?>, IProjectAuthorizedRequest
{
    public required int RequestId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.OrPermission(ProjectResource.ApprovalRequest(RequestId), ProjectAction.Read, BeaconPermissions.Approver)
    ];
}
