using Beacon.AI.Handlers.AiActors;
using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.AiActorThink"/> task: one think cycle of the actor, as the user who asked for it
/// (the service sets that user as the scope's caller before this runs). Right before the cycle it checks again that the
/// user is still the actor's creator or an Admin, and the cycle itself re-checks that they may still work with the actor's
/// data source before any LLM call or SQL; a task whose user may not is skipped
/// (<see cref="BackgroundTaskFailureCodes.Forbidden"/>), never run as the system. The cycle keeps its own cooldown (a
/// task inside it is skipped as <see cref="BackgroundTaskFailureCodes.Cooldown"/>; one for an actor paused or archived
/// meanwhile as <see cref="BackgroundTaskFailureCodes.Inactive"/>) and its LLM calls go through the
/// request queue like any other.
/// </summary>
internal sealed class AiActorThinkTaskExecutor(
    AiActorService aiActorService,
    IBeaconActorAccessor actorAccessor,
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<AiActorThinkTaskExecutor> logger) : IBackgroundTaskExecutor
{
    internal const string AlreadyRunningMessage = "A think cycle of this AI actor is already queued or running.";

    public BackgroundTaskKind Kind => BackgroundTaskKind.AiActorThink;

    public TimeSpan AbandonAfter => TimeSpan.FromMinutes(30);

    public async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
    {
        try
        {
            await AiActorOwnership.EnsureCreatorOrAdminAsync(actorAccessor, contextFactory, task.SubjectId, logger, cancellationToken);
        }
        catch (BeaconForbiddenException)
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
        }
        catch (InvalidOperationException)
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.NotFound);
        }

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var status = await context.AiActors
                .Where(x => x.Id == task.SubjectId)
                .Select(x => x.Status)
                .FirstOrDefaultAsync(cancellationToken);

            if (status != AiActorStatus.Active)
            {
                return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Inactive);
            }
        }

        var result = await aiActorService.ExecuteThinkCycleAsync(task.SubjectId, null, cancellationToken);
        if (result.Success)
        {
            return new BackgroundTaskOutcome();
        }

        var skipped = SkippedCodeOf(result.ErrorMessage, result.Phase);
        if (skipped != null)
        {
            return BackgroundTaskOutcome.Skipped(skipped);
        }

        // The execution row carries the reason; the task records a code only.
        throw new InvalidOperationException($"Think cycle of AI actor {task.SubjectId} failed.");
    }

    private static string? SkippedCodeOf(string? message, AiActorExecutionPhase phase)
    {
        if (message == null)
        {
            return null;
        }

        if (message == AiActorService.ActorUnavailableMessage)
        {
            return BackgroundTaskFailureCodes.NotFound;
        }

        if (message.StartsWith(AiActorService.ThinkCooldownMessage, StringComparison.Ordinal))
        {
            return BackgroundTaskFailureCodes.Cooldown;
        }

        if (phase != AiActorExecutionPhase.Skipped)
        {
            return null;
        }

        return message == AiActorService.OwnerNotAuthorizedMessage
            ? BackgroundTaskFailureCodes.Forbidden
            : BackgroundTaskFailureCodes.PlanPending;
    }
}
