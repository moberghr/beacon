using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.AI.Services.Ai.AiActor;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Models;

namespace Beacon.AI.Handlers.AiActors;

/// <summary>
/// An AI actor is paused, resumed, archived, refined, run on demand, or has a plan approved, rejected or sent back for
/// revision by its creator (the caller that created it, or the user an Admin made its creator) or an Admin. An actor
/// without a recorded creator is an Admin's only. To anyone but an Admin, an actor or plan that does not exist is
/// refused like one that is not theirs. Scheduled think cycles do not pass through here. Refusals are logged at Warning
/// with the actor and the caller's user id only; a security audit event for them belongs here. With the approval workflow
/// on, approving a plan takes a second person (<see cref="EnsureMayApprovePlanAsync"/>).
/// </summary>
internal static class AiActorOwnership
{
    private const string Refusal = "Only the AI actor's creator or an Admin can change or run it.";

    private const string PlanApproverRefusal = "With the approval workflow on, an AI actor's plan is approved by an Approver.";

    private const string PlanSelfApprovalRefusal = "An AI actor's plan is approved by someone other than the actor's creator.";

    private const string PlanRevisionRequesterRefusal = "An AI actor's plan is approved by someone other than who asked for its revisions.";

    public static async Task EnsureCreatorOrAdminAsync(
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory,
        int actorId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var actor = await context.AiActors
            .Where(x => x.Id == actorId)
            .Select(x =>
                new
                {
                    x.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (actor == null)
        {
            throw IsAdminOrRunAsAdmin(caller)
                ? new InvalidOperationException($"AI actor {actorId} not found.")
                : Refuse(logger, caller, actorId);
        }

        if (!IsAdminOrRunAsAdmin(caller) && !caller.Is(actor.CreatedByUserId))
        {
            throw Refuse(logger, caller, actorId);
        }
    }

    /// <summary>The same rule for a plan, through the actor that proposed it.</summary>
    public static async Task EnsureCreatorOrAdminOfPlanAsync(
        IBeaconActorAccessor actorAccessor,
        IDbContextFactory<BeaconContext> contextFactory,
        int planId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Where(x => x.Id == planId)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    x.AiActor.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (plan == null)
        {
            throw caller.IsAdmin
                ? new InvalidOperationException($"AI actor plan {planId} not found.")
                : Refuse(logger, caller, null);
        }

        if (!caller.IsOwnerOrAdmin(plan.CreatedByUserId))
        {
            throw Refuse(logger, caller, plan.AiActorId);
        }
    }

    /// <summary>
    /// Who may approve a plan, whose approval executes its actions. With the approval workflow off, the actor's creator or
    /// an Admin (<see cref="EnsureCreatorOrAdminOfPlanAsync"/>). With it on, a second person: a holder of the Approver
    /// permission (an Admin holds it) other than the actor's creator and anyone who asked for a revision of the plan
    /// (<see cref="AiActorPlanChain"/>), unless the host allows self-approval. The plan's project check (Write on the
    /// actor's data source) applies either way.
    /// </summary>
    public static async Task EnsureMayApprovePlanAsync(
        IBeaconActorAccessor actorAccessor,
        IBeaconPermissionAccessor permissionAccessor,
        IDbContextFactory<BeaconContext> contextFactory,
        BeaconConfiguration configuration,
        int planId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!configuration.ApprovalWorkflow.Enabled)
        {
            await EnsureCreatorOrAdminOfPlanAsync(actorAccessor, contextFactory, planId, logger, cancellationToken);

            return;
        }

        var caller = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var plan = await context.AiActorPlans
            .Where(x => x.Id == planId)
            .Select(x =>
                new
                {
                    x.AiActorId,
                    x.AiActor.CreatedByUserId
                })
            .FirstOrDefaultAsync(cancellationToken);

        var permissions = await permissionAccessor.GetCurrentAsync(cancellationToken);
        if (!permissions.Holds(BeaconPermissions.Approver))
        {
            throw AccessRefusal.Of(logger, caller, "AI actor", plan?.AiActorId, PlanApproverRefusal);
        }

        if (plan == null)
        {
            throw new InvalidOperationException($"AI actor plan {planId} not found.");
        }

        if (caller.UserId == null
            || (!configuration.ApprovalWorkflow.AllowSelfApproval && caller.IsRecordedAs(plan.CreatedByUserId)))
        {
            throw AccessRefusal.Of(logger, caller, "AI actor", plan.AiActorId, PlanSelfApprovalRefusal);
        }

        // Whoever asked for a revision of the plan shaped it with their feedback: they do not approve it either.
        if (!configuration.ApprovalWorkflow.AllowSelfApproval)
        {
            var revisionRequesters = await AiActorPlanChain.RevisionRequestersAsync(context, planId, cancellationToken);
            if (revisionRequesters.Any(caller.IsRecordedAs))
            {
                throw AccessRefusal.Of(logger, caller, "AI actor", plan.AiActorId, PlanRevisionRequesterRefusal);
            }
        }
    }

    /// <summary>
    /// An Admin, or the user a background task runs as (<see cref="BeaconPrincipalKind.Owner"/>) while that user is still a
    /// live Admin: a run-as caller is never built with <c>IsAdmin</c>, only with its live standing. Only a run-as caller
    /// gets this widening; every interactive caller is judged by <c>IsAdmin</c> alone.
    /// </summary>
    private static bool IsAdminOrRunAsAdmin(BeaconActor caller)
    {
        return caller.IsAdmin || (caller.Kind == BeaconPrincipalKind.Owner && caller.IsLiveAdmin);
    }

    private static BeaconForbiddenException Refuse(ILogger logger, BeaconActor caller, int? actorId)
    {
        return AccessRefusal.Of(logger, caller, "AI actor", actorId, Refusal);
    }
}
