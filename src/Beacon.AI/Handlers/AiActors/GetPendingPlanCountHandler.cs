using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Beacon.AI.Handlers.AiActors;

internal sealed class GetPendingPlanCountHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    IBeaconPermissionAccessor permissionAccessor,
    BeaconConfiguration configuration,
    IOptionsMonitor<AiActorOptions> options,
    TimeProvider timeProvider)
    : IRequestHandler<GetPendingPlanCountQuery, GetPendingPlanCountResult>
{
    public async Task<GetPendingPlanCountResult> Handle(
        GetPendingPlanCountQuery request,
        CancellationToken cancellationToken)
    {
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);
        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        var decidesOthers = caller.IsAdmin
            || (configuration.ApprovalWorkflow.Enabled && permissions.Holds(BeaconPermissions.Approver));

        if (!decidesOthers && caller.UserId == null)
        {
            return new GetPendingPlanCountResult();
        }

        // Deciding a plan writes through its actor's data source: only the actors the caller may write count.
        var writable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Write);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var actors = context.AiActors
            .AsNoTracking()
            .WhereSourceAccessible(context, writable, x => x.DataSourceId);

        if (!decidesOthers)
        {
            actors = actors.Where(x => x.CreatedByUserId == caller.UserId);
        }

        // An expired plan is no longer pending: it can no longer be approved or revised.
        var cutoff = timeProvider.GetUtcNow().UtcDateTime - TimeSpan.FromDays(options.CurrentValue.PlanExpiryDays);

        var count = await context.AiActorPlans
            .AsNoTracking()
            .Where(x => x.Status == AiActorPlanStatus.PendingApproval)
            .Where(x => x.ProposedAt > cutoff)
            .Where(x => actors.Select(y => y.Id).Contains(x.AiActorId))
            .CountAsync(cancellationToken);

        return new GetPendingPlanCountResult
        {
            Count = count
        };
    }
}
