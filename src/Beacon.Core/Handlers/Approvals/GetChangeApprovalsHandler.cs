using Beacon.Core.Authorization;
using Beacon.Core.ChangeApprovals;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Approvals;
using MediatR;

namespace Beacon.Core.Handlers.Approvals;

internal sealed class GetChangeApprovalsHandler(
    IChangeApprovalService changeApprovals,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor)
    : IRequestHandler<GetChangeApprovalsQuery, PagedList<ChangeApprovalSummary>>
{
    public async Task<PagedList<ChangeApprovalSummary>> Handle(GetChangeApprovalsQuery request, CancellationToken cancellationToken)
    {
        var scope = await ChangeApprovalScopes.OfCallerAsync(projectAccess, permissionAccessor, cancellationToken);

        return await changeApprovals.GetPendingAsync(request, request.Kind, scope, cancellationToken);
    }
}

/// <summary>
/// The pending changes of migration jobs, data contracts' Custom SQL rules and recipients, newest first unless
/// <c>sort</c> says otherwise; of one <c>kind</c> when given. An Approver (an Admin holds the permission) lists every one;
/// anyone else, as for query approvals, those of the migration jobs and data contracts it reads, never a recipient's.
/// </summary>
public record GetChangeApprovalsQuery : ListRequest, IRequest<PagedList<ChangeApprovalSummary>>, IProjectFilteredRequest
{
    public ChangeApprovalKind? Kind { get; init; }
}

/// <summary>Who the caller reads change requests as (<see cref="ChangeApprovalScope"/>).</summary>
internal static class ChangeApprovalScopes
{
    public static async Task<ChangeApprovalScope> OfCallerAsync(
        IProjectAccessEvaluator projectAccess,
        IBeaconPermissionAccessor permissionAccessor,
        CancellationToken cancellationToken)
    {
        // An Approver reviews every change request; anyone else those of the projects it reads.
        if ((await permissionAccessor.GetCurrentAsync(cancellationToken)).Holds(BeaconPermissions.Approver))
        {
            return ChangeApprovalScope.Approver;
        }

        return new ChangeApprovalScope((await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read), false);
    }
}
