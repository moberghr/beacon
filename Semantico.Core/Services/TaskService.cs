using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;
using Semantico.Core.DTOs;
using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Tasks;

namespace Semantico.Core.Services;

public class TaskService(IDbContextFactory<SemanticoContext> contextFactory) : ITaskService
{
    private readonly IDbContextFactory<SemanticoContext> _contextFactory = contextFactory;

    public async Task<int> CreateTask(int notificationId, int subscriptionId, int resultCount, CancellationToken cancellationToken)
    {
        Console.WriteLine($"TaskService.CreateTask called - NotificationId: {notificationId}, SubscriptionId: {subscriptionId}, ResultCount: {resultCount}");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Find existing unresolved task for this subscription
        var existingTask = await context.Tasks
            .Where(t => t.SubscriptionId == subscriptionId && !t.Resolved)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingTask != null)
        {
            Console.WriteLine($"Updating existing task {existingTask.Id} for subscription {subscriptionId}");
            // Update existing task with latest result count and notification time
            existingTask.LatestResultCount = resultCount;
            existingTask.LastNotificationAt = DateTime.UtcNow;

            // Auto-resolve if query returns 0 results
            if (resultCount == 0)
            {
                existingTask.Resolved = true;
                existingTask.ResolvedAt = DateTime.UtcNow;
                existingTask.ResolutionNotes = "Auto-resolved: Query returned 0 results";
            }

            // Link notification to existing task
            var notification = await context.Notifications.FindAsync(new object[] { notificationId }, cancellationToken);
            if (notification != null)
            {
                Console.WriteLine($"Linking notification {notificationId} to existing task {existingTask.Id}");
                notification.TaskId = existingTask.Id;
            }
            else
            {
                Console.WriteLine($"WARNING: Notification {notificationId} not found!");
            }

            await context.SaveChangesAsync(cancellationToken);
            Console.WriteLine($"Task {existingTask.Id} updated successfully");
            return existingTask.Id;
        }
        else
        {
            Console.WriteLine($"Creating new task for subscription {subscriptionId}");
            // Create new task
            var task = new AlertingTask
            {
                SubscriptionId = subscriptionId,
                LatestResultCount = resultCount,
                LastNotificationAt = DateTime.UtcNow,
                Resolved = resultCount == 0 // Auto-resolve if 0 results
            };

            // Auto-resolve if query returns 0 results
            if (resultCount == 0)
            {
                task.ResolvedAt = DateTime.UtcNow;
                task.ResolutionNotes = "Auto-resolved: Query returned 0 results";
            }

            context.Tasks.Add(task);
            await context.SaveChangesAsync(cancellationToken);
            Console.WriteLine($"New task {task.Id} created");

            // Link notification to new task
            var notification = await context.Notifications.FindAsync(new object[] { notificationId }, cancellationToken);
            if (notification != null)
            {
                Console.WriteLine($"Linking notification {notificationId} to new task {task.Id}");
                notification.TaskId = task.Id;
                await context.SaveChangesAsync(cancellationToken);
            }
            else
            {
                Console.WriteLine($"WARNING: Notification {notificationId} not found!");
            }

            Console.WriteLine($"Task {task.Id} created and linked successfully");
            return task.Id;
        }
    }

    public async Task<int> CreateOrUpdateTask(int subscriptionId, int resultCount, CancellationToken cancellationToken)
    {
        Console.WriteLine($"TaskService.CreateOrUpdateTask called - SubscriptionId: {subscriptionId}, ResultCount: {resultCount}");
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        // Find existing unresolved task for this subscription
        var existingTask = await context.Tasks
            .Where(t => t.SubscriptionId == subscriptionId && !t.Resolved)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingTask != null)
        {
            Console.WriteLine($"Updating existing task {existingTask.Id} for subscription {subscriptionId}");
            // Update existing task with latest result count
            existingTask.LatestResultCount = resultCount;
            existingTask.LastNotificationAt = DateTime.UtcNow;

            // Auto-resolve if query returns 0 results
            if (resultCount == 0)
            {
                existingTask.Resolved = true;
                existingTask.ResolvedAt = DateTime.UtcNow;
                existingTask.ResolutionNotes = "Auto-resolved: Query returned 0 results";
            }

            await context.SaveChangesAsync(cancellationToken);
            Console.WriteLine($"Task {existingTask.Id} updated successfully");
            return existingTask.Id;
        }

        // Don't create a new task if result count is 0 (nothing to alert on)
        if (resultCount == 0)
        {
            Console.WriteLine($"No existing task and result count is 0 - no task created");
            return 0;
        }

        Console.WriteLine($"Creating new task for subscription {subscriptionId}");
        // Create new task
        var task = new AlertingTask
        {
            SubscriptionId = subscriptionId,
            LatestResultCount = resultCount,
            LastNotificationAt = DateTime.UtcNow,
            Resolved = false
        };

        context.Tasks.Add(task);
        await context.SaveChangesAsync(cancellationToken);
        Console.WriteLine($"New task {task.Id} created successfully");
        return task.Id;
    }

    public async Task ResolveTask(int taskId, string? resolutionNotes, string? userId, CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.Tasks
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
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var task = await context.Tasks
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
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Tasks.AsQueryable();

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
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var details = await context.Tasks
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
                ResolutionNotes = t.ResolutionNotes
            })
            .FirstOrDefaultAsync(cancellationToken);

        return details;
    }

    public async Task<TaskStatisticsData> GetTaskStatistics(CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var totalTasks = await context.Tasks.CountAsync(cancellationToken);
        var unresolvedCount = await context.Tasks.Where(t => !t.Resolved).CountAsync(cancellationToken);
        var resolvedCount = await context.Tasks.Where(t => t.Resolved).CountAsync(cancellationToken);

        var resolvedTasks = await context.Tasks
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
}
