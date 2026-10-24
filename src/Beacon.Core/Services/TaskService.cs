using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.DTOs;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Tasks;
using Beacon.Core.Notifications;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Tasks;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Services;

public class TaskService(
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<TaskService> logger,
    ISecurityAuditRecorder securityAudit) : ITaskService
{
    /// <summary>
    /// The fixed note of a task resolved by a run that returned no rows. Such a resolution records no resolver: nobody
    /// resolved it.
    /// </summary>
    internal const string AutoResolveMessage = "Auto-resolved: Query returned 0 results";

    public async Task<int> CreateOrUpdateTask(int subscriptionId, int resultCount, CancellationToken cancellationToken)
    {
        logger.LogDebug("CreateOrUpdateTask called - SubscriptionId: {SubscriptionId}, ResultCount: {ResultCount}",
            subscriptionId, resultCount);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existingTask = await FindUnresolvedTaskAsync(context, subscriptionId, cancellationToken);

        if (existingTask != null)
        {
            await UpdateTaskWithResultCountAsync(context, existingTask, resultCount, cancellationToken);

            await context.SaveChangesAsync(cancellationToken);
            return existingTask.Id;
        }

        // Don't create a new task if result count is 0 (nothing to alert on)
        if (resultCount == 0)
        {
            return 0;
        }

        // Create new task (never auto-resolve new tasks in this flow)
        var task = CreateNewTask(subscriptionId, resultCount, autoResolveIfZero: false);
        context.QueryTasks.Add(task);
        await context.SaveChangesAsync(cancellationToken);

        return task.Id;
    }

    /// <summary>
    /// Finds an existing unresolved task for the given subscription.
    /// </summary>
    private static async Task<QueryTask?> FindUnresolvedTaskAsync(
        BeaconContext context,
        int subscriptionId,
        CancellationToken cancellationToken)
    {
        return await context.QueryTasks
            .Where(t => t.SubscriptionId == subscriptionId && !t.Resolved)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Updates a task with the latest result count and applies auto-resolution if result count is 0: Beacon resolves it, so
    /// the security audit records the system as the resolver, in the save that resolves it.
    /// </summary>
    private async Task UpdateTaskWithResultCountAsync(BeaconContext context, QueryTask task, int resultCount, CancellationToken cancellationToken)
    {
        task.LatestResultCount = resultCount;
        task.LastNotificationAt = DateTime.UtcNow;

        if (resultCount == 0)
        {
            task.Resolved = true;
            task.ResolvedAt = DateTime.UtcNow;
            task.ResolutionNotes = AutoResolveMessage;
            task.ResolvedByUserId = null;
            await securityAudit.StageAsync(context, AutoResolved(task), cancellationToken);
        }
    }

    // A task Beacon resolved because a run returned no rows: the system resolved it, nobody else.
    private static SecurityAuditEntry AutoResolved(QueryTask task)
    {
        return new SecurityAuditEntry(SecurityAuditActions.TaskAutoResolved)
            .On(SecurityAuditResources.Task, task.Id)
            .With(new SecurityAuditDetails().Add("subscriptionId", task.SubscriptionId))
            .By(SecurityAuditActor.System);
    }

    /// <summary>
    /// Creates a new task with the given parameters.
    /// </summary>
    private static QueryTask CreateNewTask(int subscriptionId, int resultCount, bool autoResolveIfZero)
    {
        var shouldAutoResolve = autoResolveIfZero && resultCount == 0;
        var task = new QueryTask
        {
            SubscriptionId = subscriptionId,
            LatestResultCount = resultCount,
            LastNotificationAt = DateTime.UtcNow,
            Resolved = shouldAutoResolve
        };

        if (shouldAutoResolve)
        {
            task.ResolvedAt = DateTime.UtcNow;
            task.ResolutionNotes = AutoResolveMessage;
        }

        return task;
    }

    /// <summary>
    /// Links a notification to a task.
    /// </summary>
    private async Task LinkNotificationToTaskAsync(
        BeaconContext context,
        int notificationId,
        int taskId,
        CancellationToken cancellationToken)
    {
        var notification = await context.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId, cancellationToken);
        if (notification != null)
        {
            notification.TaskId = taskId;
        }
        else
        {
            logger.LogWarning("Notification {NotificationId} not found when linking to task {TaskId}", notificationId, taskId);
        }
    }

    public async Task ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // A resolution is never overwritten: who resolved the task, when and why stays as recorded. The task is written
        // only while it is still open, with its security audit event in the same transaction.
        var resolvedAt = DateTime.UtcNow;
        var updated = await securityAudit.SaveWithEventsAsync(
            context,
            () => context.QueryTasks
                .Where(x => x.Id == taskId)
                .Where(x => !x.Resolved)
                .ExecuteUpdateAsync(
                    x => x
                        .SetProperty(y => y.Resolved, true)
                        .SetProperty(y => y.ResolvedAt, resolvedAt)
                        .SetProperty(y => y.ResolutionNotes, resolutionNotes)
                        .SetProperty(y => y.ResolvedByUserId, userId),
                    cancellationToken),
            written => written == 0 ? [] : [new SecurityAuditEntry(SecurityAuditActions.TaskResolved).On(SecurityAuditResources.Task, taskId)],
            cancellationToken);

        if (updated > 0)
        {
            return;
        }

        var exists = await context.QueryTasks
            .Where(x => x.Id == taskId)
            .AnyAsync(cancellationToken);

        throw exists
            ? new InvalidOperationException($"Task {taskId} is already resolved.")
            : new BeaconException($"Task {taskId} not found");
    }

    // The reads and the comment write below live in their handlers now; these forwarders keep the 5.0 surface for one
    // release. A handler's Read/Add method touches only the context factory, so the caller-bound dependencies stay unset.

    [Obsolete("Use GetTasksQuery; removed in 6.0.")]
    public Task<PagedList<TaskData>> GetTasks(GetTasksRequest request, CancellationToken cancellationToken)
    {
        return new GetTasksHandler(contextFactory, null!).Read(request, cancellationToken);
    }

    [Obsolete("Use GetTaskDetailQuery; removed in 6.0.")]
    public Task<TaskDetailsData?> GetTaskDetails(int taskId, string? currentUserId, CancellationToken cancellationToken)
    {
        return new GetTaskDetailHandler(contextFactory, null!, null!).Read(taskId, currentUserId, cancellationToken);
    }

    [Obsolete("Use GetTaskExecutionsQuery; removed in 6.0.")]
    public Task<List<QueryExecutionSummary>> GetTaskExecutionHistory(int taskId, StoredRunScope runScope, CancellationToken cancellationToken)
    {
        return new GetTaskExecutionsHandler(contextFactory, null!).Read(taskId, runScope, cancellationToken);
    }

    [Obsolete("Use GetTaskRelatedQuery; removed in 6.0.")]
    public Task<List<RelatedTaskSummary>> GetRelatedTasks(int taskId, CancellationToken cancellationToken)
    {
        return new GetTaskRelatedHandler(contextFactory).Read(taskId, cancellationToken);
    }

    [Obsolete("Use GetTaskResultHistoryQuery; removed in 6.0.")]
    public Task<List<ResultCountDataPoint>> GetResultCountHistory(int taskId, StoredRunScope runScope, CancellationToken cancellationToken)
    {
        return new GetTaskResultHistoryHandler(contextFactory, null!).Read(taskId, runScope, cancellationToken);
    }

    [Obsolete("Use GetTaskCommentsQuery; removed in 6.0.")]
    public Task<List<CommentData>> GetTaskComments(int taskId, CancellationToken cancellationToken)
    {
        return new GetTaskCommentsHandler(contextFactory).Read(taskId, cancellationToken);
    }

    [Obsolete("Use AddTaskCommentCommand; removed in 6.0.")]
    public Task<int> AddTaskComment(int taskId, string content, string? userId, string? userName, CancellationToken cancellationToken)
    {
        return new AddTaskCommentHandler(contextFactory, null!).Add(taskId, content, userId, userName, cancellationToken);
    }
}
