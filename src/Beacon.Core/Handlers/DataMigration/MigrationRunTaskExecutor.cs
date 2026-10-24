using Beacon.Core.Authorization;
using Beacon.Core.BackgroundTasks;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Models.DataMigration;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.DataMigration;

/// <summary>
/// Runs a <see cref="BackgroundTaskKind.MigrationRun"/> task: a manual run of one migration job, as the user who started it.
/// Right before the run it checks again that the user still holds the MigrationOperator permission (a stored role live; a
/// permission the identity provider granted at the start only shortly after it, <see cref="BackgroundTaskRequesterPermissions"/>)
/// and meets the job's demands (<see cref="MigrationJobAccess"/>), and skips the task
/// (<see cref="BackgroundTaskFailureCodes.Forbidden"/>, on record as a refused <c>migrations.job_run_ended</c>) when not; the
/// write policy is checked again too, and a task given up as abandoned meanwhile does not start the run. The run is <see cref="IMigrationService.ExecuteMigrationJob"/>, unchanged:
/// it claims the job (one run at a time), holds the destination lock, stops at the job's timeout, never cancels its commit
/// and always closes its history row. A run refused because another is in progress skips the task
/// (<see cref="BackgroundTaskFailureCodes.Busy"/>); a run that failed fails it (<see cref="BackgroundTaskFailureCodes.RunFailed"/>,
/// the history row says why); one that completed reports the rows that failed. A task left running by a job runner that
/// stopped is reconciled with the job's run history when the next run starts or its state is read
/// (<see cref="HasEndedAsync"/>), instead of holding the job until <see cref="AbandonAfter"/>.
/// </summary>
internal sealed class MigrationRunTaskExecutor(
    IMigrationService migrationService,
    MigrationJobAccess access,
    MigrationWritePolicy writePolicy,
    BackgroundTaskRequesterPermissions permissions,
    IBackgroundTaskHeartbeat heartbeat,
    IDbContextFactory<BeaconContext> contextFactory,
    TimeProvider timeProvider) : IReconcilingBackgroundTaskExecutor
{
    /// <summary>
    /// How long a running task may go without reaching its run, and without a history row of its run once it reached it: its
    /// checks before the run claims the job take seconds, so a task with none after this never reached the run.
    /// </summary>
    internal static readonly TimeSpan ClaimGrace = TimeSpan.FromMinutes(5);

    /// <summary>How long after its run's history row closed a task is left to record its own outcome.</summary>
    internal static readonly TimeSpan FinishGrace = TimeSpan.FromMinutes(1);

    /// <summary>What the run's history row names as its trigger.</summary>
    internal const string ExecutionContext = "manual-background";

    public BackgroundTaskKind Kind => BackgroundTaskKind.MigrationRun;

    // As long as the longest run can last, with the margin after which the run itself is closed as abandoned.
    public TimeSpan AbandonAfter => MigrationJobSettings.RunInProgressWindow(MigrationJobSettings.MaxTimeoutMinutes);

    public async Task<BackgroundTaskOutcome> ExecuteAsync(BackgroundTaskContext task, CancellationToken cancellationToken)
    {
        writePolicy.DemandEnabled();

        if (!await permissions.HoldsAsync(BeaconPermissions.MigrationOperator, task.TaskId, cancellationToken))
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
        }

        try
        {
            await access.DemandForJobAsync(task.SubjectId, cancellationToken);
        }
        catch (BeaconException ex) when (ex is BeaconForbiddenException or BeaconNotFoundException)
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Forbidden);
        }

        await writePolicy.DemandTargetsForJobAsync(task.SubjectId, cancellationToken);

        // Marks the moment the task reaches its run (reconciliation counts only runs from here) and stops a task that was
        // given up meanwhile from starting one.
        if (!await heartbeat.BeatAsync(task.TaskId, cancellationToken))
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Abandoned);
        }

        var response = await migrationService.ExecuteMigrationJob(
            new ExecuteMigrationJobRequest(task.SubjectId, IsManualExecution: true, ExecutionContext: ExecutionContext),
            cancellationToken);
        if (response.RunInProgress)
        {
            return BackgroundTaskOutcome.Skipped(BackgroundTaskFailureCodes.Busy);
        }

        if (response.Status is MigrationStatus.Failed or MigrationStatus.Cancelled)
        {
            throw new BackgroundTaskFailedException(response.ExecutionId == 0
                ? BackgroundTaskFailureCodes.Failed
                : BackgroundTaskFailureCodes.RunFailed);
        }

        return new BackgroundTaskOutcome(response.RowsFailed);
    }

    /// <summary>
    /// The task's run has ended by the job's run history: the task never reached its run within <see cref="ClaimGrace"/> of
    /// its start, or every run of the job started since it reached it (its heartbeat; runs started before are another
    /// trigger's) is closed (a minute ago or more) or was left running past the job's timeout and margin (abandoned), or none
    /// started within <see cref="ClaimGrace"/> of it; also when the job no longer exists.
    /// </summary>
    public async Task<bool> HasEndedAsync(int subjectId, DateTime startedTime, DateTime? heartbeatTime, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var timeoutMinutes = await context.MigrationJobs
            .IgnoreQueryFilters()
            .Where(x => x.Id == subjectId)
            .Select(x => (int?)x.TimeoutMinutes)
            .FirstOrDefaultAsync(cancellationToken);
        if (timeoutMinutes == null)
        {
            return true;
        }

        if (heartbeatTime == null)
        {
            return startedTime < now - ClaimGrace;
        }

        var runs = await context.MigrationExecutions
            .AsNoTracking()
            .Where(x => x.MigrationJobId == subjectId)
            .Where(x => x.StartedAt >= heartbeatTime.Value)
            .Select(x =>
                new
                {
                    x.Status,
                    x.StartedAt,
                    x.CompletedAt
                })
            .ToListAsync(cancellationToken);
        if (runs.Count == 0)
        {
            return heartbeatTime.Value < now - ClaimGrace;
        }

        var inProgressSince = now - MigrationJobSettings.RunInProgressWindow(timeoutMinutes.Value);
        if (runs.Any(x => x.Status == MigrationStatus.Running && x.StartedAt > inProgressSince))
        {
            return false;
        }

        return runs
            .Where(x => x.Status != MigrationStatus.Running)
            .All(x => x.CompletedAt == null || x.CompletedAt < now - FinishGrace);
    }

    /// <summary>
    /// A run the task refused before it started, on record as a refused <c>migrations.job_run_ended</c> with the task and the
    /// reason; none for <see cref="BackgroundTaskFailureCodes.Busy"/>, which the migration service records itself.
    /// </summary>
    public SecurityAuditEntry? SkippedEvent(BackgroundTaskContext task, string code)
        => code == BackgroundTaskFailureCodes.Busy
            ? null
            : new SecurityAuditEntry(SecurityAuditActions.MigrationJobRunEnded)
                .On(SecurityAuditResources.MigrationJob, task.SubjectId)
                .Ending(SecurityAuditOutcomes.Refused)
                .With(new SecurityAuditDetails()
                    .Add("taskId", task.TaskId)
                    .AddCode("reason", code));
}
