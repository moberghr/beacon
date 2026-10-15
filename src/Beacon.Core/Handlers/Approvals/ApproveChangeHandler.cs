using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using MediatR;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class ApproveChangeHandler(IChangeApprovalService changeApprovals)
    : IRequestHandler<ApproveChangeCommand>
{
    public async Task Handle(ApproveChangeCommand request, CancellationToken cancellationToken)
    {
        // The reviewer is the request's caller, never an id the request carries; the decision names people by ids that
        // are not returned to the caller.
        await changeApprovals.ApproveAsync(request.RequestId, request.Comment, request.ExpectedPayloadHash, cancellationToken);
    }
}

/// <summary>
/// Approves a pending change of a migration job, a data contract's Custom SQL rules or a recipient, as the caller: never
/// its requester (403), for exactly the payload the reviewer was shown (<see cref="ExpectedPayloadHash"/>, required; 409
/// otherwise), while the resource still holds exactly the definition the request records (409 otherwise).
/// </summary>
[ProjectScopeExempt("gated by the Approver permission: approving a change is an approval decision, and an Approver reads every change request", Policy = BeaconPermissions.ApproverPolicy)]
public record ApproveChangeCommand : IRequest
{
    public required int RequestId { get; init; }

    public string? Comment { get; init; }

    /// <summary>The <c>PayloadHash</c> of the request's detail the reviewer was shown.</summary>
    public string? ExpectedPayloadHash { get; init; }
}
