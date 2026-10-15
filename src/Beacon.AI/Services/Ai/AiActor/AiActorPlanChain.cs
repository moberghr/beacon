using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>
/// The people who asked for a revision of a plan: a revision is generated as a new plan whose parent was marked
/// RevisionRequested by that person, so the plan's chain of parents names them. Their feedback shaped the plan, so with
/// the approval workflow on none of them approves it, nor the query changes its approval makes.
/// </summary>
internal static class AiActorPlanChain
{
    // A chain is a handful of plans; the bound only stops a damaged one (a cycle) from looping.
    private const int MaxDepth = 100;

    /// <summary>The recorded reviewers of the plans in <paramref name="planId"/>'s chain of parents that asked for a revision.</summary>
    public static async Task<IReadOnlyList<string>> RevisionRequestersAsync(BeaconContext context, int planId, CancellationToken cancellationToken)
    {
        var requesters = new List<string>();
        var parentId = await context.AiActorPlans
            .Where(x => x.Id == planId)
            .Select(x => x.ParentPlanId)
            .FirstOrDefaultAsync(cancellationToken);

        for (var depth = 0; parentId != null && depth < MaxDepth; depth++)
        {
            var parent = await context.AiActorPlans
                .Where(x => x.Id == parentId)
                .Select(x =>
                    new
                    {
                        x.ParentPlanId,
                        x.Status,
                        x.ReviewedByUserId
                    })
                .FirstOrDefaultAsync(cancellationToken);
            if (parent == null)
            {
                break;
            }

            if (parent.Status == AiActorPlanStatus.RevisionRequested && !string.IsNullOrEmpty(parent.ReviewedByUserId))
            {
                requesters.Add(parent.ReviewedByUserId);
            }

            parentId = parent.ParentPlanId;
        }

        return requesters;
    }
}
