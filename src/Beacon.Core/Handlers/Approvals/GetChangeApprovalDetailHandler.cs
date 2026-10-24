using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Models.Approvals;
using MediatR;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetChangeApprovalDetailHandler(
    IChangeApprovalService changeApprovals,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor)
    : IRequestHandler<GetChangeApprovalDetailQuery, ChangeApprovalDetail?>
{
    public async Task<ChangeApprovalDetail?> Handle(GetChangeApprovalDetailQuery request, CancellationToken cancellationToken)
    {
        var scope = await ChangeApprovalScopes.OfCallerAsync(projectAccess, permissionAccessor, cancellationToken);

        return await changeApprovals.GetDetailAsync(request.RequestId, scope, cancellationToken);
    }
}

/// <summary>
/// A change request with what it changes (the resource before and as proposed, credentials masked), the payload hash an
/// approval names, and whether the caller may approve it now. An Approver (an Admin holds the permission) reads every
/// one but the LLM-connection ones, which only a live Admin reads; anyone else, as for query approvals, one of a migration
/// job or data contract it reads (never a recipient's or a connection's), and any other is not found.
/// </summary>
[ProjectScopeExempt("scope-filtered in the service: an Approver reads every change request but the LLM-connection ones, which only a live Admin reads; anyone else one of a migration job or data contract whose data sources it reads (never a recipient's or a connection's), and any other is not found")]
public record GetChangeApprovalDetailQuery : IRequest<ChangeApprovalDetail?>
{
    public required int RequestId { get; init; }
}
