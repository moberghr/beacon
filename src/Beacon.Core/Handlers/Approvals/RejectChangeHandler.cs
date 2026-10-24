using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using MediatR;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class RejectChangeHandler(IChangeApprovalService changeApprovals)
    : IRequestHandler<RejectChangeCommand>
{
    public async Task Handle(RejectChangeCommand request, CancellationToken cancellationToken)
    {
        // The reviewer is the request's caller, never an id the request carries; the decision names people by ids that
        // are not returned to the caller.
        await changeApprovals.RejectAsync(request.RequestId, request.Comment, cancellationToken);
    }
}

/// <summary>
/// Rejects a pending change as the caller (its requester may withdraw it; a change of the LLM connection only a live
/// Admin, the requester included, 403 otherwise); 409 when it is no longer pending. The resource keeps the definition it
/// holds and stays inactive until that is changed back, or changed and approved.
/// </summary>
[ProjectScopeExempt("gated by the Approver permission: rejecting a change is an approval decision, and an Approver reads every change request but the LLM-connection ones, which only a live Admin reads", Policy = BeaconPermissions.ApproverPolicy)]
public record RejectChangeCommand : IRequest
{
    public required int RequestId { get; init; }

    public string? Comment { get; init; }
}
