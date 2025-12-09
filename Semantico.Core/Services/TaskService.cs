using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.DTOs;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Tasks;

namespace Semantico.Core.Services;

public class TaskService(IDbContextFactory<SemanticoContext> contextFactory, ILogger<TaskService> logger) : ITaskService
{
    private const string AutoResolveMessage = "Auto-resolved: Query returned 0 results";

    public async Task<int> CreateTask(int notificationId, int subscriptionId, int resultCount, CancellationToken cancellationToken)
    {
        logger.LogDebug("CreateTask called - NotificationId: {NotificationId}, SubscriptionId: {SubscriptionId}, ResultCount: {ResultCount}",
            notificationId, subscriptionId, resultCount);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existingTask = await FindUnresolvedTaskAsync(context, subscriptionId, cancellationToken);

        if (existingTask != null)
        {
            logger.LogDebug("Updating existing task {TaskId} for subscription {SubscriptionId}", existingTask.Id, subscriptionId);
            UpdateTaskWithResultCount(existingTask, resultCount);
            await LinkNotificationToTaskAsync(context, notificationId, existingTask.Id, cancellationToken);
            await context.SaveChangesAsync(cancellationToken);
            return existingTask.Id;
        }

        // Create new task
        var task = CreateNewTask(subscriptionId, resultCount, autoResolveIfZero: true);
        context.QueryTasks.Add(task);
        await context.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Created new task {TaskId} for subscription {SubscriptionId}", task.Id, subscriptionId);

        await LinkNotificationToTaskAsync(context, notificationId, task.Id, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return task.Id;
    }

    public async Task<int> CreateOrUpdateTask(int subscriptionId, int resultCount, CancellationToken cancellationToken)
    {
        logger.LogDebug("CreateOrUpdateTask called - SubscriptionId: {SubscriptionId}, ResultCount: {ResultCount}",
            subscriptionId, resultCount);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var existingTask = await FindUnresolvedTaskAsync(context, subscriptionId, cancellationToken);

        if (existingTask != null)
        {
            UpdateTaskWithResultCount(existingTask, resultCount);
            
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
        SemanticoContext context,
        int subscriptionId,
        CancellationToken cancellationToken)
    {
        return await context.QueryTasks
            .Where(t => t.SubscriptionId == subscriptionId && !t.Resolved)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Updates a task with the latest result count and applies auto-resolution if result count is 0.
    /// </summary>
    private static void UpdateTaskWithResultCount(QueryTask task, int resultCount)
    {
        task.LatestResultCount = resultCount;
        task.LastNotificationAt = DateTime.UtcNow;

        if (resultCount == 0)
        {
            task.Resolved = true;
            task.ResolvedAt = DateTime.UtcNow;
            task.ResolutionNotes = AutoResolveMessage;
        }
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
        SemanticoContext context,
        int notificationId,
        int taskId,
        CancellationToken cancellationToken)
    {
        var notification = await context.Notifications.FindAsync(new object[] { notificationId }, cancellationToken);
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

        var task = await context.QueryTasks
            .FindAsync(new object[] { taskId }, cancellationToken)
            ?? throw new SemanticoException($"Task {taskId} not found");

        // Update task resolution fields
        task.Resolved = true;
        task.ResolvedAt = DateTime.UtcNow;
        task.ResolutionNotes = resolutionNotes;
        task.ResolvedByUserId = userId;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReopenTask(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.QueryTasks
            .FindAsync(new object[] { taskId }, cancellationToken)
            ?? throw new SemanticoException($"Task {taskId} not found");

        if (!task.Resolved)
        {
            // Idempotent: already unresolved, no action needed
            return;
        }

        // Reopen task
        task.Resolved = false;
        task.ResolvedAt = null;
        task.ResolutionNotes = null;
        task.ResolvedByUserId = null;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TaskListData> GetTasks(GetTasksRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.QueryTasks.AsQueryable();

        // Apply filters
        if (request.SubscriptionId.HasValue)
            query = query.Where(t => t.SubscriptionId == request.SubscriptionId.Value);

        if (request.Resolved.HasValue)
            query = query.Where(t => t.Resolved == request.Resolved.Value);

        // Project to DTO (EF Core automatically generates necessary JOINs)
        var pagedTasks = await query
            .Select(t => new TaskData
            {
                Id = t.Id,
                SubscriptionName = t.Subscription.Query.Name,
                QueryName = t.Subscription.Query.Name,
                LatestResultCount = t.LatestResultCount,
                LastNotificationAt = t.LastNotificationAt,
                NotificationCount = t.Notifications.Count,
                CreatedAt = t.CreatedTime,
                Resolved = t.Resolved,
                ResolvedAt = t.ResolvedAt,
                ResolvedByUserName = null, // TODO: lookup from user service when auth integrated
                // Count executions since task creation
                ExecutionCount = context.QueryExecutionHistory
                    .Count(qeh => qeh.SubscriptionId == t.SubscriptionId),
                // Count distinct result counts to show volatility
                UniqueResultCounts = context.QueryExecutionHistory
                    .Where(qeh => qeh.SubscriptionId == t.SubscriptionId)
                    .Select(qeh => qeh.ResultCount)
                    .Distinct()
                    .Count()
            })
            .ToPagedListAsync(request, cancellationToken);

        return new TaskListData
        {
            Data = pagedTasks.Items,
            TotalCount = pagedTasks.TotalCount
        };
    }

    public async Task<TaskDetailsData?> GetTaskDetails(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var details = await context.QueryTasks
            .Where(t => t.Id == taskId)
            .Select(t => new TaskDetailsData
            {
                Id = t.Id,
                Subscription = new SubscriptionSummary(
                    t.Subscription.Id,
                    t.Subscription.Query.Name,
                    null
                ),
                LatestResultCount = t.LatestResultCount,
                LastNotificationAt = t.LastNotificationAt,
                NotificationCount = t.Notifications.Count,
                Notifications = t.Notifications
                    .OrderByDescending(n => n.SentAt)
                    .Select(n => new NotificationSummary(
                        n.Id,
                        n.SentAt,
                        n.QueryExecutionHistory.ResultCount,
                        n.Results
                    ))
                    .ToList(),
                CreatedAt = t.CreatedTime,
                Resolved = t.Resolved,
                ResolvedAt = t.ResolvedAt,
                ResolvedByUserId = t.ResolvedByUserId,
                ResolvedByUserName = null, // TODO: lookup when auth integrated
                ResolutionNotes = t.ResolutionNotes,
                QueryId = t.Subscription.QueryId,
                QueryName = t.Subscription.Query.Name
            })
            .FirstOrDefaultAsync(cancellationToken);

        return details;
    }

    public async Task<TaskStatisticsData> GetTaskStatistics(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var totalTasks = await context.QueryTasks.CountAsync(cancellationToken);
        var unresolvedCount = await context.QueryTasks.Where(t => !t.Resolved).CountAsync(cancellationToken);
        var resolvedCount = await context.QueryTasks.Where(t => t.Resolved).CountAsync(cancellationToken);

        var resolvedTasks = await context.QueryTasks
            .Where(t => t.Resolved && t.ResolvedAt.HasValue)
            .Select(t => new { t.CreatedTime, t.ResolvedAt })
            .ToListAsync(cancellationToken);

        double? averageResolutionTimeHours = null;
        if (resolvedTasks.Any())
        {
            var totalHours = resolvedTasks
                .Where(t => t.ResolvedAt.HasValue)
                .Sum(t => (t.ResolvedAt!.Value - t.CreatedTime).TotalHours);
            averageResolutionTimeHours = totalHours / resolvedTasks.Count;
        }

        return new TaskStatisticsData
        {
            TotalTasks = totalTasks,
            UnresolvedCount = unresolvedCount,
            ResolvedCount = resolvedCount,
            AverageResolutionTimeHours = averageResolutionTimeHours
        };
    }

    public async Task<List<QueryExecutionSummary>> GetTaskExecutionHistory(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get the subscription ID for this task
        var subscriptionId = await context.QueryTasks
            .Where(t => t.Id == taskId)
            .Select(t => t.SubscriptionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscriptionId == 0)
            return new List<QueryExecutionSummary>();

        var executions = await context.QueryExecutionHistory
            .Where(qeh => qeh.SubscriptionId == subscriptionId)
            .OrderByDescending(qeh => qeh.CreatedTime)
            .Take(50) // Limit to last 50 executions
            .Select(qeh => new QueryExecutionSummary(
                qeh.Id,
                qeh.CreatedTime,
                qeh.ExecutionTimeMs,
                qeh.NotificationStatus,
                qeh.ResultCount
            ))
            .ToListAsync(cancellationToken);

        return executions;
    }

    public async Task<List<RelatedTaskSummary>> GetRelatedTasks(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get the query ID for this task's subscription
        var queryId = await context.QueryTasks
            .Where(t => t.Id == taskId)
            .Select(t => t.Subscription.QueryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (queryId == 0)
            return new List<RelatedTaskSummary>();

        // Find all tasks from subscriptions using the same query, excluding the current task
        var relatedTasks = await context.QueryTasks
            .IgnoreQueryFilters() // Include archived tasks
            .Where(t => t.Subscription.QueryId == queryId && t.Id != taskId)
            .OrderByDescending(t => t.CreatedTime)
            .Take(20) // Limit to last 20 related tasks
            .Select(t => new RelatedTaskSummary(
                t.Id,
                t.CreatedTime,
                t.LatestResultCount,
                t.Resolved,
                t.ResolvedAt
            ))
            .ToListAsync(cancellationToken);

        return relatedTasks;
    }

    public async Task<List<ResultCountDataPoint>> GetResultCountHistory(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get the subscription ID for this task
        var subscriptionId = await context.QueryTasks
            .Where(t => t.Id == taskId)
            .Select(t => t.SubscriptionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (subscriptionId == 0)
            return new List<ResultCountDataPoint>();

        // Get result counts from execution history for charting
        var resultHistory = await context.QueryExecutionHistory
            .Where(qeh => qeh.SubscriptionId == subscriptionId)
            .OrderBy(qeh => qeh.CreatedTime)
            .Select(qeh => new ResultCountDataPoint(qeh.CreatedTime, qeh.ResultCount))
            .ToListAsync(cancellationToken);

        return resultHistory;
    }

    public async Task<List<CommentData>> GetTaskComments(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var comments = await context.Comments
            .Where(c => c.EntityType == EntityType.Task && c.EntityId == taskId)
            .OrderByDescending(c => c.CreatedTime)
            .Select(c => new CommentData(c.Id, c.Content, c.UserName, c.CreatedTime))
            .ToListAsync(cancellationToken);

        return comments;
    }

    public async Task<int> AddTaskComment(int taskId, string content, string? userId, string? userName, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Verify task exists
        var taskExists = await context.QueryTasks.AnyAsync(t => t.Id == taskId, cancellationToken);
        if (!taskExists)
            throw new SemanticoException($"Task {taskId} not found");

        var comment = new Comment
        {
            EntityType = EntityType.Task,
            EntityId = taskId,
            Content = content,
            UserId = userId,
            UserName = userName
        };

        context.Comments.Add(comment);
        await context.SaveChangesAsync(cancellationToken);

        return comment.Id;
    }
}
