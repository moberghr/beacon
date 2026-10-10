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

public record GetApprovalDetailQuery : IRequest<ApprovalRequestDetail?>, IProjectAuthorizedRequest
{
    public required int RequestId { get; init; }

    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.ApprovalRequest(RequestId), ProjectAction.Read)
    ];
}
