using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Worker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Beacon.Core.BackgroundTasks;

/// <summary>
/// <see cref="IBackgroundTaskService"/> over the <c>BackgroundTasks</c> table. Each step of a task's life (queued, handed
/// over, running, finished) is its own short write, outside the unit of work of whoever calls it: the job runs elsewhere
/// and later, so its state cannot commit with the request. A step writes only from the state it expects (queued to running,
/// running to finished), so a task given up as abandoned is never written back as running or finished.
/// </summary>
internal sealed class BackgroundTaskService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEnumerable<IBackgroundTaskExecutor> executors,
    IBeaconScheduler scheduler,
    IBeaconActorAccessor actorAccessor,
    IBeaconRequestPrincipal requestPrincipal,
    TimeProvider timeProvider,
    ILogger<BackgroundTaskService> logger) : IBackgroundTaskService
{
    public async Task<BackgroundTaskTicket?> StartAsync(BackgroundTaskKind kind, int subjectId, CancellationToken cancellationToken)
    {
        var executor = ExecutorOf(kind)
            ?? throw new InvalidOperationException($"No background task executor is registered for {kind}.");
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var abandonedBefore = now - executor.AbandonAfter;

        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // A task its job runner stopped running would hold the subject forever: give it up once it is past its limit.
            await context.BackgroundTasks
                .Where(x => x.Kind == kind)
                .Where(x => x.SubjectId == subjectId)
                .Where(x => x.State == BackgroundTaskState.Queued || x.State == BackgroundTaskState.Running)
                .Where(x => (x.StartedTime ?? x.CreatedTime) < abandonedBefore)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.State, BackgroundTaskState.Failed)
                        .SetProperty(y => y.FinishedTime, now)
                        .SetProperty(y => y.FailureCode, BackgroundTaskFailureCodes.Abandoned),
                    cancellationToken);
        }

        var task = new BackgroundTask
        {
            Kind = kind,
            SubjectId = subjectId,
            RequestedByUserId = (await actorAccessor.GetCurrentAsync(cancellationToken)).BeaconUserId,
            State = BackgroundTaskState.Queued,
            CreatedTime = now
        };
        if (!await TryAddAsync(task, cancellationToken))
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
                        x.RequestedByUserId
                    })
                .FirstOrDefaultAsync(cancellationToken);
            if (row == null)
            {
                logger.LogWarning("Background task {TaskId} does not exist; nothing to run.", taskId);
                return;
            }

            task = new BackgroundTaskContext(taskId, row.SubjectId, row.RequestedByUserId);
            kind = row.Kind;
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

        await FinishAsync(taskId, BackgroundTaskState.Running, BackgroundTaskState.Succeeded, null, outcome.WarningCount);
    }

    public async Task<BackgroundTaskStatus?> GetStatusAsync(BackgroundTaskKind kind, int subjectId, int taskId, CancellationToken cancellationToken)
    {
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

    // The id SignalR addresses the caller by (Beacon.Api's HubUserIdProvider: the NameIdentifier claim), so the job's
    // JobStatusChanged events reach the user who started it and nobody else.
    private string? HubUserId()
    {
        var id = requestPrincipal.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value;

        return string.IsNullOrWhiteSpace(id) ? null : id;
    }

    private IBackgroundTaskExecutor? ExecutorOf(BackgroundTaskKind kind)
        => executors
            .Where(x => x.Kind == kind)
            .FirstOrDefault();

    // The unique index on unfinished tasks refuses a second queued or running task of the kind on the subject.
    private async Task<bool> TryAddAsync(BackgroundTask task, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        context.BackgroundTasks.Add(task);
        try
        {
            await context.SaveChangesAsync(cancellationToken);

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
