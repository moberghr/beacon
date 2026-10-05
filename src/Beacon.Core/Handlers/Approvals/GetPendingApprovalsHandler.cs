using Beacon.Core.Helpers;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetPendingApprovalsHandler(IQueryApprovalService approvalService)
    : IRequestHandler<GetPendingApprovalsQuery, PagedList<ApprovalRequestSummary>>
{
    public Task<PagedList<ApprovalRequestSummary>> Handle(GetPendingApprovalsQuery request, CancellationToken cancellationToken) =>
        approvalService.GetPendingApprovalsAsync(request, request.QueryId, cancellationToken);
}

/// <summary>Pending approval requests, newest first unless <c>sort</c> says otherwise.</summary>
public record GetPendingApprovalsQuery : ListRequest, IRequest<PagedList<ApprovalRequestSummary>>
{
    public int? QueryId { get; init; }
}
