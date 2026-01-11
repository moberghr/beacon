using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class StatisticsService(IDbContextFactory<SemanticoContext> contextFactory) : IStatisticsService
{
    public async Task<DashboardStatisticsData> GetDashboardStatistics(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var notifications = await context.QueryExecutionHistory
            .GroupBy(x => 1)
            .Select(x => new NotificationDateStatisticsData()
            {
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Get anomaly statistics
        var twentyFourHoursAgo = DateTime.UtcNow.AddHours(-24);
        var totalAnomalies = await context.AnomalyEvents.CountAsync(cancellationToken);
        var recentAnomalies = await context.AnomalyEvents
            .CountAsync(a => a.DetectedTime >= twentyFourHoursAgo, cancellationToken);
        var activeAnomalyConfigs = await context.AnomalyConfigs
            .CountAsync(c => c.Enabled, cancellationToken);

        // Get notification channel breakdown
        var notificationsByChannel = await context.Notifications
            .GroupBy(n => n.Recipient.NotificationType)
            .Select(g => new { Channel = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Channel.ToString(), x => x.Count, cancellationToken);

        // Get recent activities (last 10)
        var recentExecutions = await context.QueryExecutionHistory
            .OrderByDescending(h => h.CreatedTime)
            .Take(5)
            .Select(h => new RecentActivityItem
            {
                Type = "Query Execution",
                Description = $"Query '{h.Subscription.Query.Name}' executed",
                Timestamp = h.CreatedTime,
                Icon = "Search",
                Link = $"/semantico/subscriptions/{h.SubscriptionId}",
                Status = h.NotificationStatus == NotificationStatus.NotificationSent ? "Notification Sent" : null
            })
            .ToListAsync(cancellationToken);

        var recentAnomalyEvents = await context.AnomalyEvents
            .OrderByDescending(a => a.DetectedTime)
            .Take(5)
            .Select(a => new RecentActivityItem
            {
                Type = "Anomaly Detected",
                Description = $"Anomaly detected in subscription '{a.Subscription.Query.Name}'",
                Timestamp = a.DetectedTime,
                Icon = "Warning",
                Link = $"/semantico/subscriptions/{a.SubscriptionId}",
                Status = a.Severity
            })
            .ToListAsync(cancellationToken);

        var recentActivities = recentExecutions
            .Concat(recentAnomalyEvents)
            .OrderByDescending(a => a.Timestamp)
            .Take(10)
            .ToList();

        // Get top subscriptions by execution count (last 30 days)
        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var topSubscriptions = await context.QueryExecutionHistory
            .Where(h => h.CreatedTime >= thirtyDaysAgo)
            .GroupBy(h => new { h.SubscriptionId, h.Subscription.Query.Name })
            .Select(g => new TopSubscriptionItem
            {
                SubscriptionId = g.Key.SubscriptionId,
                SubscriptionName = g.Key.Name,
                ExecutionCount = g.Count(),
                NotificationCount = g.Count(h => h.NotificationStatus == NotificationStatus.NotificationSent),
                LastExecuted = g.Max(h => h.CreatedTime)
            })
            .OrderByDescending(s => s.ExecutionCount)
            .Take(5)
            .ToListAsync(cancellationToken);

        // Get execution time statistics (only successful executions with valid times)
        var executionTimeStats = await context.QueryExecutionHistory
            .Where(h => h.ExecutionTimeMs > 0)
            .GroupBy(x => 1)
            .Select(g => new
            {
                AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
                MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
                MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
            })
            .FirstOrDefaultAsync(cancellationToken);

        // Get execution time history (last 30 days, grouped by date, only successful executions)
        var executionTimeHistory = await context.QueryExecutionHistory
            .Where(h => h.CreatedTime >= thirtyDaysAgo && h.ExecutionTimeMs > 0)
            .GroupBy(h => h.CreatedTime.Date)
            .Select(g => new ExecutionTimeDataPoint
            {
                Date = g.Key,
                AvgExecutionTimeMs = g.Average(h => h.ExecutionTimeMs),
                MinExecutionTimeMs = g.Min(h => h.ExecutionTimeMs),
                MaxExecutionTimeMs = g.Max(h => h.ExecutionTimeMs)
            })
            .OrderBy(x => x.Date)
            .ToListAsync(cancellationToken);

        return new DashboardStatisticsData
        {
            TotalSubscriptions = await context.Subscriptions.IgnoreQueryFilters().CountAsync(cancellationToken),
            TotalQueries = await context.Queries.CountAsync(cancellationToken),
            TotalQueriesExecuted = notifications?.TotalQueries ?? 0,
            TotalNotificationsSent = notifications?.NotificationsSent ?? 0,
            ActiveSubscriptions = await context.Subscriptions.CountAsync(cancellationToken),
            TotalMigrationJobs = await context.MigrationJobs.CountAsync(cancellationToken),
            EnabledMigrationJobs = await context.MigrationJobs.CountAsync(m => m.IsEnabled, cancellationToken),
            TotalMigrationExecutions = await context.MigrationExecutions.CountAsync(cancellationToken),
            SuccessfulMigrationExecutions = await context.MigrationExecutions.CountAsync(m => m.Status == MigrationStatus.Completed, cancellationToken),
            TotalTasks = await context.QueryTasks.CountAsync(cancellationToken),
            UnresolvedTasks = await context.QueryTasks.CountAsync(t => !t.Resolved, cancellationToken),
            ResolvedTasks = await context.QueryTasks.CountAsync(t => t.Resolved, cancellationToken),
            TotalAnomaliesDetected = totalAnomalies,
            ActiveAnomalyConfigs = activeAnomalyConfigs,
            AnomaliesLast24Hours = recentAnomalies,
            NotificationsByChannel = notificationsByChannel,
            RecentActivities = recentActivities,
            TopSubscriptions = topSubscriptions,
            TotalDataSources = await context.DataSources.CountAsync(cancellationToken),
            TotalRecipients = await context.Recipients.CountAsync(cancellationToken),
            AvgExecutionTimeMs = executionTimeStats?.AvgExecutionTimeMs ?? 0,
            MinExecutionTimeMs = executionTimeStats?.MinExecutionTimeMs ?? 0,
            MaxExecutionTimeMs = executionTimeStats?.MaxExecutionTimeMs ?? 0,
            ExecutionTimeHistory = executionTimeHistory
        };
    }
}

public interface IStatisticsService
{
    Task<DashboardStatisticsData> GetDashboardStatistics(CancellationToken cancellationToken);
}