using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.ControlTower;
using Semantico.Core.Helpers;

namespace Semantico.Core.Services;

internal class ControlTowerService(
    IDbContextFactory<SemanticoContext> contextFactory,
    IMemoryCache memoryCache) : IControlTowerService
{
    private const string StatisticsCacheKey = "ControlTower_Statistics";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<ControlTowerHealthListData> GetSubscriptionHealthOverview(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        // Build query with filters
        var query = context.Subscriptions.AsQueryable();

        if (request.FolderId.HasValue)
        {
            query = query.Where(s => s.Query.FolderId == request.FolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchKeyword))
        {
            query = query.Where(s => s.Query.Name.Contains(request.SearchKeyword));
        }

        // Get base subscription data (simple projection that EF Core can translate reliably)
        var subscriptions = await query.Select(s => new
        {
            s.Id,
            s.QueryId,
            QueryName = s.Query.Name,
            FolderPath = s.Query.Folder != null ? s.Query.Folder.Path : null,
            s.CreateTasks,
            s.StoreResults,
            s.AiActorId,
            AiActorName = s.AiActor != null ? s.AiActor.Name : null
        }).ToListAsync(cancellationToken);

        var subscriptionIds = subscriptions.Select(s => s.Id).ToList();

        // Load execution history separately (last 30 days for success rate)
        var executions = await context.QueryExecutionHistory
            .Where(h => subscriptionIds.Contains(h.SubscriptionId) && h.CreatedTime >= thirtyDaysAgo)
            .Select(h => new { h.SubscriptionId, h.NotificationStatus, h.CreatedTime, h.ResultCount })
            .ToListAsync(cancellationToken);

        var executionsBySubscription = executions
            .GroupBy(h => h.SubscriptionId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Load last execution per subscription (regardless of time window)
        // Use Max aggregates instead of First() which EF Core can't translate in GroupBy
        var lastExecutionTimes = await context.QueryExecutionHistory
            .Where(h => subscriptionIds.Contains(h.SubscriptionId))
            .GroupBy(h => h.SubscriptionId)
            .Select(g => new
            {
                SubscriptionId = g.Key,
                LastCreatedTime = g.Max(h => h.CreatedTime)
            })
            .ToListAsync(cancellationToken);

        // Load the actual last execution details by matching on the max timestamp
        var lastExecFilters = lastExecutionTimes
            .Select(x => new { x.SubscriptionId, x.LastCreatedTime })
            .ToList();

        var lastExecutionDetails = new List<(int SubscriptionId, NotificationStatus NotificationStatus, DateTime CreatedTime, int ResultCount)>();
        if (lastExecFilters.Count > 0)
        {
            var allLastExecs = await context.QueryExecutionHistory
                .Where(h => subscriptionIds.Contains(h.SubscriptionId))
                .OrderByDescending(h => h.CreatedTime)
                .Select(h => new { h.SubscriptionId, h.NotificationStatus, h.CreatedTime, h.ResultCount })
                .ToListAsync(cancellationToken);

            lastExecutionDetails = allLastExecs
                .GroupBy(h => h.SubscriptionId)
                .Select(g => g.First())
                .Select(h => (h.SubscriptionId, h.NotificationStatus, h.CreatedTime, h.ResultCount))
                .ToList();
        }

        var lastExecutionBySubscription = lastExecutionDetails
            .ToDictionary(x => x.SubscriptionId);

        // Load task metrics separately
        var taskMetrics = await context.QueryTasks
            .Where(t => subscriptionIds.Contains(t.SubscriptionId))
            .GroupBy(t => t.SubscriptionId)
            .Select(g => new
            {
                SubscriptionId = g.Key,
                UnresolvedCount = g.Count(t => !t.Resolved),
                TotalCount = g.Count()
            })
            .ToDictionaryAsync(x => x.SubscriptionId, cancellationToken);

        // Load anomaly metrics separately
        var anomalyCountsBySubscription = await context.AnomalyEvents
            .Where(a => subscriptionIds.Contains(a.SubscriptionId) && a.DetectedTime >= thirtyDaysAgo)
            .GroupBy(a => a.SubscriptionId)
            .Select(g => new { SubscriptionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SubscriptionId, x => x.Count, cancellationToken);

        // Load anomaly sparkline data separately
        var anomalySparklineData = await context.AnomalyEvents
            .Where(a => subscriptionIds.Contains(a.SubscriptionId) && a.DetectedTime >= thirtyDaysAgo)
            .GroupBy(a => new { a.SubscriptionId, Date = a.DetectedTime.Date })
            .Select(g => new { g.Key.SubscriptionId, g.Key.Date, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var sparklineBySubscription = anomalySparklineData
            .GroupBy(x => x.SubscriptionId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Date)
                    .Select(x => new AnomalySparklinePoint { Date = x.Date, AnomalyCount = x.Count })
                    .ToList());

        // Load anomaly detection configs
        var anomalyEnabledSubscriptions = await context.AnomalyConfigs
            .Where(c => subscriptionIds.Contains(c.SubscriptionId) && c.Enabled)
            .Select(c => c.SubscriptionId)
            .ToListAsync(cancellationToken);

        var anomalyEnabledSet = anomalyEnabledSubscriptions.ToHashSet();

        // Build health data
        var healthData = subscriptions.Select(item =>
        {
            var itemExecutions = executionsBySubscription.GetValueOrDefault(item.Id, []);
            var totalExecutions = itemExecutions.Count;
            var successfulExecutions = itemExecutions.Count(e =>
                e.NotificationStatus == NotificationStatus.NotificationSent ||
                e.NotificationStatus == NotificationStatus.NoResults ||
                e.NotificationStatus == NotificationStatus.NotificationSilenced);
            var failedExecutions = totalExecutions - successfulExecutions;
            var successRate = totalExecutions > 0 ? (double)successfulExecutions / totalExecutions * 100 : 100;
            var healthStatus = CalculateHealthStatus(successfulExecutions, totalExecutions);
            var hasLastExecution = lastExecutionBySubscription.TryGetValue(item.Id, out var lastExecution);
            var tasks = taskMetrics.GetValueOrDefault(item.Id);

            return new ControlTowerSubscriptionHealthData
            {
                SubscriptionId = item.Id,
                QueryName = item.QueryName,
                DataSourceName = null, // Multi-step queries can have multiple data sources
                FolderPath = item.FolderPath,
                HealthStatus = healthStatus,
                TotalExecutions = totalExecutions,
                SuccessfulExecutions = successfulExecutions,
                FailedExecutions = failedExecutions,
                SuccessRate = successRate,
                LastExecutionTime = hasLastExecution ? lastExecution.CreatedTime : null,
                LastExecutionStatus = hasLastExecution ? lastExecution.NotificationStatus : null,
                LastResultCount = hasLastExecution ? lastExecution.ResultCount : null,
                UnresolvedTaskCount = tasks?.UnresolvedCount ?? 0,
                TotalTaskCount = tasks?.TotalCount ?? 0,
                AnomalyCount30Days = anomalyCountsBySubscription.GetValueOrDefault(item.Id, 0),
                AnomalySparkline = sparklineBySubscription.GetValueOrDefault(item.Id, []),
                IsActive = true,
                CreateTasks = item.CreateTasks,
                StoreResults = item.StoreResults,
                HasAnomalyDetection = anomalyEnabledSet.Contains(item.Id),
                AiActorId = item.AiActorId,
                AiActorName = item.AiActorName
            };
        }).ToList();

        // Apply post-query filters (after health calculation)
        if (request.HealthStatus.HasValue)
        {
            healthData = healthData.Where(h => h.HealthStatus == request.HealthStatus.Value).ToList();
        }

        if (request.HasUnresolvedTasks.HasValue && request.HasUnresolvedTasks.Value)
        {
            healthData = healthData.Where(h => h.UnresolvedTaskCount > 0).ToList();
        }

        // Get total count after filtering
        var totalCount = healthData.Count;

        // Apply pagination
        healthData = healthData
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToList();

        return new ControlTowerHealthListData
        {
            Data = healthData,
            TotalCount = totalCount
        };
    }

    public async Task<ControlTowerStatistics> GetControlTowerStatistics(CancellationToken cancellationToken)
    {
        // Try to get from cache
        if (memoryCache.TryGetValue(StatisticsCacheKey, out ControlTowerStatistics? cachedStatistics))
        {
            return cachedStatistics!;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);

        // Get all active subscription IDs
        var subscriptionIds = await context.Subscriptions
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        // Get execution history for the last 30 days, grouped by subscription
        var executionsBySubscription = await context.QueryExecutionHistory
            .Where(h => h.CreatedTime >= thirtyDaysAgo && subscriptionIds.Contains(h.SubscriptionId))
            .GroupBy(h => h.SubscriptionId)
            .Select(g => new
            {
                SubscriptionId = g.Key,
                Statuses = g.Select(h => h.NotificationStatus).ToList()
            })
            .ToListAsync(cancellationToken);

        var executionLookup = executionsBySubscription.ToDictionary(x => x.SubscriptionId, x => x.Statuses);

        var healthyCount = 0;
        var warningCount = 0;
        var criticalCount = 0;
        var totalSuccessful = 0;
        var totalExecutions = 0;

        foreach (var subscriptionId in subscriptionIds)
        {
            var statuses = executionLookup.GetValueOrDefault(subscriptionId, []);
            var executions = statuses.Count;
            var successful = CountSuccessfulExecutions(statuses);

            totalExecutions += executions;
            totalSuccessful += successful;

            var healthStatus = CalculateHealthStatus(successful, executions);

            switch (healthStatus)
            {
                case HealthStatus.Green:
                    healthyCount++;
                    break;
                case HealthStatus.Amber:
                    warningCount++;
                    break;
                case HealthStatus.Red:
                    criticalCount++;
                    break;
            }
        }

        var overallSuccessRate = totalExecutions > 0 ? (double)totalSuccessful / totalExecutions * 100 : 100;

        var unresolvedTasks = await context.QueryTasks.CountAsync(t => !t.Resolved, cancellationToken);
        var anomalies30Days = await context.AnomalyEvents
            .CountAsync(a => a.DetectedTime >= thirtyDaysAgo, cancellationToken);

        var statistics = new ControlTowerStatistics
        {
            TotalSubscriptions = subscriptionIds.Count,
            HealthySubscriptions = healthyCount,
            WarningSubscriptions = warningCount,
            CriticalSubscriptions = criticalCount,
            TotalUnresolvedTasks = unresolvedTasks,
            TotalAnomalies30Days = anomalies30Days,
            OverallSuccessRate = overallSuccessRate
        };

        // Cache for 30 seconds
        memoryCache.Set(StatisticsCacheKey, statistics, CacheDuration);

        return statistics;
    }

    private static HealthStatus CalculateHealthStatus(int successCount, int totalExecutions)
    {
        if (totalExecutions == 0)
        {
            return HealthStatus.Green; // No data yet, assume healthy
        }

        var successRate = (double)successCount / totalExecutions * 100;

        return successRate switch
        {
            >= 90 => HealthStatus.Green,
            >= 70 => HealthStatus.Amber,
            _ => HealthStatus.Red
        };
    }

    private static int CountSuccessfulExecutions(IEnumerable<NotificationStatus> statuses)
    {
        return statuses.Count(status =>
            status == NotificationStatus.NotificationSent ||
            status == NotificationStatus.NoResults ||
            status == NotificationStatus.NotificationSilenced);
    }

}

public interface IControlTowerService
{
    Task<ControlTowerHealthListData> GetSubscriptionHealthOverview(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken);

    Task<ControlTowerStatistics> GetControlTowerStatistics(CancellationToken cancellationToken);
}
