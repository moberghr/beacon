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
    private const double GreenThreshold = 90;
    private const double AmberThreshold = 70;

    public async Task<ControlTowerHealthListData> GetSubscriptionHealthOverview(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var thirtyDaysAgo = DateTime.UtcNow.AddDays(-30);
        var successStatuses = new[]
        {
            NotificationStatus.NotificationSent,
            NotificationStatus.NoResults,
            NotificationStatus.NotificationSilenced
        };

        // Step 1: Build base subscription query with DB-side filters
        var query = context.Subscriptions.AsQueryable();

        if (request.FolderId.HasValue)
        {
            query = query.Where(s => s.Query.FolderId == request.FolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchKeyword))
        {
            query = query.Where(s => s.Query.Name.Contains(request.SearchKeyword));
        }

        // Step 2: Project subscription data with execution stats aggregated in DB
        var subscriptionStatsQuery = query.Select(s => new
        {
            s.Id,
            QueryName = s.Query.Name,
            FolderPath = s.Query.Folder != null ? s.Query.Folder.Path : null,
            s.CreateTasks,
            s.StoreResults,
            s.AiActorId,
            AiActorName = s.AiActor != null ? s.AiActor.Name : null,
            // Execution stats aggregated in DB (last 30 days)
            TotalExecutions = s.QueryExecutionHistory!.Count(h => h.CreatedTime >= thirtyDaysAgo),
            SuccessfulExecutions = s.QueryExecutionHistory!.Count(h =>
                h.CreatedTime >= thirtyDaysAgo && successStatuses.Contains(h.NotificationStatus)),
            // Task metrics via correlated subquery (no navigation property on Subscription)
            UnresolvedTaskCount = context.QueryTasks.Count(t => t.SubscriptionId == s.Id && !t.Resolved),
            TotalTaskCount = context.QueryTasks.Count(t => t.SubscriptionId == s.Id)
        });

        // Step 3: Apply HealthStatus and HasUnresolvedTasks filters in DB where possible
        if (request.HasUnresolvedTasks is true)
        {
            subscriptionStatsQuery = subscriptionStatsQuery.Where(s => s.UnresolvedTaskCount > 0);
        }

        // HealthStatus filter requires success rate calculation — we push it to DB with conditional logic
        if (request.HealthStatus.HasValue)
        {
            subscriptionStatsQuery = request.HealthStatus.Value switch
            {
                HealthStatus.Green => subscriptionStatsQuery.Where(s =>
                    s.TotalExecutions == 0 ||
                    (double)s.SuccessfulExecutions / s.TotalExecutions * 100 >= GreenThreshold),
                HealthStatus.Amber => subscriptionStatsQuery.Where(s =>
                    s.TotalExecutions > 0 &&
                    (double)s.SuccessfulExecutions / s.TotalExecutions * 100 >= AmberThreshold &&
                    (double)s.SuccessfulExecutions / s.TotalExecutions * 100 < GreenThreshold),
                HealthStatus.Red => subscriptionStatsQuery.Where(s =>
                    s.TotalExecutions > 0 &&
                    (double)s.SuccessfulExecutions / s.TotalExecutions * 100 < AmberThreshold),
                _ => subscriptionStatsQuery
            };
        }

        // Step 4: Get total count and paginate in DB
        var totalCount = await subscriptionStatsQuery.CountAsync(cancellationToken);

        var pagedSubscriptions = await subscriptionStatsQuery
            .OrderBy(s => s.QueryName)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        if (pagedSubscriptions.Count == 0)
        {
            return new ControlTowerHealthListData { Data = [], TotalCount = totalCount };
        }

        // Step 5: Load detail data only for the current page of subscription IDs
        var pageIds = pagedSubscriptions.Select(s => s.Id).ToList();

        // Last execution per subscription (one row per subscription via GroupBy + Max in DB)
        var lastExecutionBySubscription = new Dictionary<int, (NotificationStatus NotificationStatus, DateTime CreatedTime, int ResultCount)>();

        if (pageIds.Count > 0)
        {
            var lastExecs = await context.QueryExecutionHistory
                .Where(h => pageIds.Contains(h.SubscriptionId))
                .GroupBy(h => h.SubscriptionId)
                .Select(g => g.OrderByDescending(h => h.CreatedTime).First())
                .Select(h => new { h.SubscriptionId, h.NotificationStatus, h.CreatedTime, h.ResultCount })
                .ToListAsync(cancellationToken);

            foreach (var exec in lastExecs)
            {
                lastExecutionBySubscription[exec.SubscriptionId] = (exec.NotificationStatus, exec.CreatedTime, exec.ResultCount);
            }
        }

        // Anomaly sparkline data — only for current page (counts derived from this, no separate query needed)
        var anomalySparklineData = await context.AnomalyEvents
            .Where(a => pageIds.Contains(a.SubscriptionId) && a.DetectedTime >= thirtyDaysAgo)
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

        // Derive anomaly counts from sparkline data (avoids a separate DB round-trip)
        var anomalyCountsBySubscription = sparklineBySubscription
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Sum(p => p.AnomalyCount));

        // Anomaly detection configs — only for current page
        var anomalyEnabledSet = (await context.AnomalyConfigs
            .Where(c => pageIds.Contains(c.SubscriptionId) && c.Enabled)
            .Select(c => c.SubscriptionId)
            .ToListAsync(cancellationToken))
            .ToHashSet();

        // Step 6: Build health data from pre-aggregated stats
        var healthData = pagedSubscriptions.Select(item =>
        {
            var failedExecutions = item.TotalExecutions - item.SuccessfulExecutions;
            var successRate = item.TotalExecutions > 0 ? (double)item.SuccessfulExecutions / item.TotalExecutions * 100 : 100;
            var healthStatus = CalculateHealthStatus(item.SuccessfulExecutions, item.TotalExecutions);
            var hasLastExecution = lastExecutionBySubscription.TryGetValue(item.Id, out var lastExecution);

            return new ControlTowerSubscriptionHealthData
            {
                SubscriptionId = item.Id,
                QueryName = item.QueryName,
                DataSourceName = null, // Multi-step queries can have multiple data sources
                FolderPath = item.FolderPath,
                HealthStatus = healthStatus,
                TotalExecutions = item.TotalExecutions,
                SuccessfulExecutions = item.SuccessfulExecutions,
                FailedExecutions = failedExecutions,
                SuccessRate = successRate,
                LastExecutionTime = hasLastExecution ? lastExecution.CreatedTime : null,
                LastExecutionStatus = hasLastExecution ? lastExecution.NotificationStatus : null,
                LastResultCount = hasLastExecution ? lastExecution.ResultCount : null,
                UnresolvedTaskCount = item.UnresolvedTaskCount,
                TotalTaskCount = item.TotalTaskCount,
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
            >= GreenThreshold => HealthStatus.Green,
            >= AmberThreshold => HealthStatus.Amber,
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
