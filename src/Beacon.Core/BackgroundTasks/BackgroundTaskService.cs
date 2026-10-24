using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.SecurityAudit;
using Beacon.Core.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// <see cref="IBackgroundTaskService"/> over the <c>BackgroundTasks</c> table. Each step of a task's life (queued, handed
/// over, running, finished) is its own short write, outside the unit of work of whoever calls it: the job runs elsewhere
/// and later, so its state cannot commit with the request. A step writes only from the state it expects (queued to running,
/// running to finished), so a task given up as abandoned is never written back as running or finished. Off a request, the
/// run acts as the user who started the task (<see cref="IRunAsScope"/>), never as the system; a task started by a caller
/// without a stored user (a host without Beacon user management, where such an owner runs as the system anyway) keeps the
/// job's system scope.
/// </summary>
internal sealed class BackgroundTaskService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEnumerable<IBackgroundTaskExecutor> executors,
    IBeaconScheduler scheduler,
    IBeaconActorAccessor actorAccessor,
    IBeaconRequestPrincipal requestPrincipal,
    IRunAsScope runAs,
    ISecurityAuditRecorder securityAudit,
    TimeProvider timeProvider,
    ILogger<BackgroundTaskService> logger) : IBackgroundTaskService
{
    /// <summary>
    /// A queued task the job runner never got (the process stopped between recording it and handing it over, or the
    /// hand-over's job id was never written) is given up this long after it was recorded, not after its kind's limit: the
    /// hand-over takes milliseconds.
    /// </summary>
    internal static readonly TimeSpan LostAfter = TimeSpan.FromMinutes(5);

    public async Task<BackgroundTaskTicket?> StartAsync(
        BackgroundTaskKind kind,
        int subjectId,
        Func<int, SecurityAuditEntry>? auditEvent,
        CancellationToken cancellationToken)
    {
        var executor = ExecutorOf(kind)
            ?? throw new InvalidOperationException($"No background task executor is registered for {kind}.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await GiveUpAbandonedAsync(kind, subjectId, executor, now, cancellationToken);

        var task = new BackgroundTask
        {
            Kind = kind,
            SubjectId = subjectId,
            RequestedByUserId = (await actorAccessor.GetCurrentAsync(cancellationToken)).BeaconUserId,
            State = BackgroundTaskState.Queued,
            CreatedTime = now
        };
        if (!await TryAddAsync(task, auditEvent, cancellationToken))
        {
            return null;
        }

        string? jobId;
        try
        {
            jobId = await scheduler.EnqueueBackgroundTask(task.Id, HubUserId());
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Background task {TaskId} ({Kind}) could not be handed to the job runner.", task.Id, kind);
            await FinishAsync(task.Id, BackgroundTaskState.Queued, BackgroundTaskState.Failed, BackgroundTaskFailureCodes.EnqueueFailed, null);
            throw;
        }

        if (jobId == null)
        {
            return await RunInRequestAsync(task.Id, kind, subjectId, cancellationToken);
        }

        await using (var context = await contextFactory.CreateDbContextAsync(CancellationToken.None))
        {
            // The job may already be running: the job id is written whatever the state.
            await context.BackgroundTasks
                .Where(x => x.Id == task.Id)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.JobId, jobId), CancellationToken.None);
        }

        return new BackgroundTaskTicket(task.Id, jobId, BackgroundTaskState.Queued);
    }

    public async Task ExecuteAsync(int taskId, CancellationToken cancellationToken)
    {
        BackgroundTaskContext? task;
        BackgroundTaskKind kind;
        string? requestedByExternalId;
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            var row = await context.BackgroundTasks
                .AsNoTracking()
                .Where(x => x.Id == taskId)
                .Select(x =>
                    new
                    {
                        x.Kind,
                        x.SubjectId,
                        x.RequestedByUserId,
                        RequestedByExternalId = context.Users
                            .Where(y => y.Id == x.RequestedByUserId)
                            .Select(y => y.ExternalId)
                            .FirstOrDefault()
                    })
                .FirstOrDefaultAsync(cancellationToken);
            if (row == null)
            {
                logger.LogWarning("Background task {TaskId} does not exist; nothing to run.", taskId);
                return;
            }

            task = new BackgroundTaskContext(taskId, row.SubjectId, row.RequestedByUserId);
            kind = row.Kind;
            requestedByExternalId = row.RequestedByExternalId;
        }

        if (!await ClaimAsync(taskId, cancellationToken))
        {
            logger.LogInformation("Background task {TaskId} is no longer queued; not run.", taskId);
            return;
        }

        var executor = ExecutorOf(kind);
        if (executor == null)
        {
            logger.LogError("No background task executor is registered for {Kind}; task {TaskId} failed.", kind, taskId);
            await FinishAsync(taskId, BackgroundTaskState.Running, BackgroundTaskState.Failed, BackgroundTaskFailureCodes.NoExecutor, null);
            return;
        }

        // Work done for a user runs as that user as they are now (§1.13), set before anything in the scope resolves its caller;
        // in a request (a host without a job runner) the request's caller is the user who started it.
        if (!requestPrincipal.HasRequest && task.RequestedByUserId != null)
        {
            if (string.IsNullOrEmpty(requestedByExternalId))
            {
                logger.LogWarning(
                    "Background task {TaskId} ({Kind}) skipped: the user who started it ({UserId}) no longer exists.",
                    taskId,
                    kind,
                    task.RequestedByUserId);
                await FinishAsync(taskId, BackgroundTaskState.Running, BackgroundTaskState.Skipped, BackgroundTaskFailureCodes.Forbidden, null);
                return;
            }

            runAs.RunAs(requestedByExternalId);
        }

        BackgroundTaskOutcome outcome;
        try
        {
            outcome = await executor.ExecuteAsync(task, cancellationToken);
        }
        catch (Exception ex)
        {
            var code = BackgroundTaskFailureCodes.Of(ex);
            logger.LogWarning(ex, "Background task {TaskId} ({Kind}) failed: {FailureCode}.", taskId, kind, code);
            await FinishAsync(taskId, BackgroundTaskState.Running, BackgroundTaskState.Failed, code, null);
            throw;
        }

        if (outcome.SkippedCode != null)
        {
            logger.LogInformation("Background task {TaskId} ({Kind}) skipped: {FailureCode}.", taskId, kind, outcome.SkippedCode);
            await FinishAsync(taskId, BackgroundTaskState.Running, BackgroundTaskState.Skipped, outcome.SkippedCode, null);
            return;
        }

        await FinishAsync(taskId, BackgroundTaskState.Running, BackgroundTaskState.Succeeded, null, outcome.WarningCount);
    }

    public async Task<BackgroundTaskStatus?> GetStatusAsync(BackgroundTaskKind kind, int subjectId, int taskId, CancellationToken cancellationToken)
    {
        var executor = ExecutorOf(kind);
        if (executor != null)
        {
            await GiveUpAbandonedAsync(kind, subjectId, executor, timeProvider.GetUtcNow().UtcDateTime, cancellationToken);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        return await context.BackgroundTasks
            .AsNoTracking()
            .Where(x => x.Id == taskId)
            .Where(x => x.Kind == kind)
            .Where(x => x.SubjectId == subjectId)
            .Select(x =>
                new BackgroundTaskStatus(
                    x.Id,
                    x.JobId,
                    x.State,
                    x.CreatedTime,
                    x.StartedTime,
                    x.FinishedTime,
                    x.FailureCode,
                    x.WarningCount))
            .FirstOrDefaultAsync(cancellationToken);
    }

    // The id the hub addresses the caller by (the same resolver as Beacon.Api's HubUserIdProvider), so the job's
    // JobStatusChanged events reach the user who started it and nobody else; none for an API key or an MCP caller.
    private string? HubUserId()
    {
        var id = BeaconHubUserId.Of(requestPrincipal.User);

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    // A task its job runner stopped running would hold the subject forever: give it up once it is past its kind's limit,
    // or, still queued without a job, past LostAfter.
    private async Task GiveUpAbandonedAsync(BackgroundTaskKind kind, int subjectId, IBackgroundTaskExecutor executor, DateTime now, CancellationToken cancellationToken)
    {
        var abandonedBefore = now - executor.AbandonAfter;
        var lostBefore = now - (LostAfter < executor.AbandonAfter ? LostAfter : executor.AbandonAfter);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        await context.BackgroundTasks
            .Where(x => x.Kind == kind)
            .Where(x => x.SubjectId == subjectId)
            .Where(x => x.State == BackgroundTaskState.Queued || x.State == BackgroundTaskState.Running)
            .Where(x => (x.StartedTime ?? x.CreatedTime) < abandonedBefore
                || (x.State == BackgroundTaskState.Queued && x.JobId == null && x.CreatedTime < lostBefore))
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.State, BackgroundTaskState.Failed)
                    .SetProperty(y => y.FinishedTime, now)
                    .SetProperty(y => y.FailureCode, BackgroundTaskFailureCodes.Abandoned),
                cancellationToken);

        if (executor is IReconcilingBackgroundTaskExecutor reconciling)
        {
            await GiveUpEndedAsync(context, kind, subjectId, reconciling, now, cancellationToken);
        }
    }

    // A running task whose work has ended by its own record (the job runner stopped after or before it) is given up now;
    // written only while it is still running from the same start, so a task that finished meanwhile keeps its outcome.
    private static async Task GiveUpEndedAsync(
        BeaconContext context,
        BackgroundTaskKind kind,
        int subjectId,
        IReconcilingBackgroundTaskExecutor executor,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var running = await context.BackgroundTasks
            .AsNoTracking()
            .Where(x => x.Kind == kind)
            .Where(x => x.SubjectId == subjectId)
            .Where(x => x.State == BackgroundTaskState.Running)
            .Where(x => x.StartedTime != null)
            .Select(x =>
                new
                {
                    x.Id,
                    x.StartedTime
                })
            .FirstOrDefaultAsync(cancellationToken);
        if (running == null || !await executor.HasEndedAsync(subjectId, running.StartedTime!.Value, cancellationToken))
        {
            return;
        }

        await context.BackgroundTasks
            .Where(x => x.Id == running.Id)
            .Where(x => x.State == BackgroundTaskState.Running)
            .Where(x => x.StartedTime == running.StartedTime)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.State, BackgroundTaskState.Failed)
                    .SetProperty(y => y.FinishedTime, now)
                    .SetProperty(y => y.FailureCode, BackgroundTaskFailureCodes.Abandoned),
                cancellationToken);
    }

    private IBackgroundTaskExecutor? ExecutorOf(BackgroundTaskKind kind)
        => executors
            .Where(x => x.Kind == kind)
            .FirstOrDefault();

    // The unique index on unfinished tasks refuses a second queued or running task of the kind on the subject. The start's
    // audit event commits with the task, before the job runner has it.
    private async Task<bool> TryAddAsync(BackgroundTask task, Func<int, SecurityAuditEntry>? auditEvent, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.BackgroundTasks.Add(task);
        try
        {
            if (auditEvent == null)
            {
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                await securityAudit.SaveWithEventsAsync(context, () => [auditEvent(task.Id)], cancellationToken);
            }

            return true;
        }
        catch (DbUpdateException ex) when (DbUniqueViolation.IsUniqueViolation(ex))
        {
            return false;
        }
    }

    // No job runner: the request runs it. Its failure is on the task (and in the log), which the ticket reports.
    private async Task<BackgroundTaskTicket> RunInRequestAsync(int taskId, BackgroundTaskKind kind, int subjectId, CancellationToken cancellationToken)
    {
        try
        {
            await ExecuteAsync(taskId, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Recorded on the task and logged by ExecuteAsync.
        }

        var status = await GetStatusAsync(kind, subjectId, taskId, CancellationToken.None);

        return new BackgroundTaskTicket(taskId, null, status?.State ?? BackgroundTaskState.Failed);
    }

    private async Task<bool> ClaimAsync(int taskId, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var claimed = await context.BackgroundTasks
            .Where(x => x.Id == taskId)
            .Where(x => x.State == BackgroundTaskState.Queued)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.State, BackgroundTaskState.Running)
                    .SetProperty(y => y.StartedTime, now),
                cancellationToken);

        return claimed == 1;
    }

    // Written whether or not the caller was cancelled: a task must not stay running because its run was.
    private async Task FinishAsync(int taskId, BackgroundTaskState from, BackgroundTaskState to, string? failureCode, int? warningCount)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        await using var context = await contextFactory.CreateDbContextAsync(CancellationToken.None);
        await context.BackgroundTasks
            .Where(x => x.Id == taskId)
            .Where(x => x.State == from)
            .ExecuteUpdateAsync(
                x => x
                    .SetProperty(y => y.State, to)
                    .SetProperty(y => y.FinishedTime, now)
                    .SetProperty(y => y.FailureCode, failureCode)
                    .SetProperty(y => y.WarningCount, warningCount),
                CancellationToken.None);
    }
}
