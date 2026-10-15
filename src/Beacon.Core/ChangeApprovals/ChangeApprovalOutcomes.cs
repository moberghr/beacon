using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Approvals;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.ChangeApprovals;

/// <summary>
/// The last decision on governed resources' changes, shown with a resource that waits for approval while no request of
/// it is pending: a rejection (with the reviewer's comment) says to change it and save again, a superseded request that a
/// newer save replaced.
/// </summary>
internal static class ChangeApprovalOutcomes
{
    /// <summary>The latest decided (or superseded) request of each of <paramref name="resourceIds"/> of <paramref name="kind"/> that has one.</summary>
    public static async Task<Dictionary<int, ChangeApprovalOutcome>> LastDecidedAsync(
        BeaconContext context,
        ChangeApprovalKind kind,
        IReadOnlyCollection<int> resourceIds,
        CancellationToken cancellationToken)
    {
        if (resourceIds.Count == 0)
        {
            return [];
        }

        var decided = await context.ChangeApprovalRequests
            .Where(x => x.Kind == kind)
            .Where(x => resourceIds.Contains(x.ResourceId))
            .Where(x => x.Status != ApprovalStatus.Pending)
            .Select(x =>
                new
                {
                    x.ResourceId,
                    Outcome = new ChangeApprovalOutcome
                    {
                        RequestId = x.Id,
                        Status = x.Status,
                        Comment = x.ReviewComment,
                        DecidedAt = x.ReviewedAt
                    }
                })
            .ToListAsync(cancellationToken);

        return decided
            .GroupBy(x => x.ResourceId)
            .ToDictionary(x => x.Key, x => x.MaxBy(y => y.Outcome.RequestId)!.Outcome);
    }
}
