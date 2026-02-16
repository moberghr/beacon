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
        var query = context.Subscriptions
            .Where(s => s.ArchivedTime == null) // Only active subscriptions
            .AsQueryable();

        // Apply filters
        if (request.FolderId.HasValue)
        {
            query = query.Where(s => s.Query.FolderId == request.FolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchKeyword))
        {
            query = query.Where(s => s.Query.Name.Contains(request.SearchKeyword));
        }

        // Project to health data
        var healthDataQuery = query.Select(s => new
        {
            s.Id,
            s.QueryId,
            QueryName = s.Query.Name,
            DataSourceName = (string?)null, // Multi-step queries can have multiple data sources
            FolderPath = s.Query.Folder != null ? s.Query.Folder.Path : null,
            s.CreateTasks,
            s.StoreResults,
            s.AiActorId,
            AiActorName = s.AiActor != null ? s.AiActor.Name : null,

            // Execution history (last 30 days)
            Executions = s.QueryExecutionHistory!
                .Where(h => h.CreatedTime >= thirtyDaysAgo)
                .Select(h => new
                {
                    h.NotificationStatus,
                    h.CreatedTime,
                    h.ResultCount
                })
                .ToList(),

            // Task metrics
            UnresolvedTaskCount = context.QueryTasks
                .Count(t => t.SubscriptionId == s.Id && !t.Resolved),
            TotalTaskCount = context.QueryTasks
                .Count(t => t.SubscriptionId == s.Id),

            // Anomaly metrics
            AnomalyCount30Days = context.AnomalyEvents
                .Count(a => a.SubscriptionId == s.Id && a.DetectedTime >= thirtyDaysAgo),

            AnomalySparkline = context.AnomalyEvents
                .Where(a => a.SubscriptionId == s.Id && a.DetectedTime >= thirtyDaysAgo)
                .GroupBy(a => a.DetectedTime.Date)
                .Select(g => new AnomalySparklinePoint
                {
                    Date = g.Key,
                    AnomalyCount = g.Count()
                })
                .OrderBy(x => x.Date)
                .ToList(),

            HasAnomalyDetection = context.AnomalyConfigs
                .Any(c => c.SubscriptionId == s.Id && c.Enabled)
        });

        // Get all data first (we'll filter and paginate after health calculation)
        var allData = await healthDataQuery.ToListAsync(cancellationToken);

        // Calculate health metrics for each subscription
        var healthData = allData.Select(item =>
        {
            var totalExecutions = item.Executions.Count;
            var successfulExecutions = CountSuccessfulExecutions(item.Executions);
            var failedExecutions = totalExecutions - successfulExecutions;
            var successRate = totalExecutions > 0 ? (double)successfulExecutions / totalExecutions * 100 : 100;
            var healthStatus = CalculateHealthStatus(successfulExecutions, totalExecutions);

            var lastExecution = item.Executions.OrderByDescending(e => e.CreatedTime).FirstOrDefault();

            return new ControlTowerSubscriptionHealthData
            {
                SubscriptionId = item.Id,
                QueryName = item.QueryName,
                DataSourceName = item.DataSourceName,
                FolderPath = item.FolderPath,
                HealthStatus = healthStatus,
                TotalExecutions = totalExecutions,
                SuccessfulExecutions = successfulExecutions,
                FailedExecutions = failedExecutions,
                SuccessRate = successRate,
                LastExecutionTime = lastExecution?.CreatedTime,
                LastExecutionStatus = lastExecution?.NotificationStatus,
                LastResultCount = lastExecution?.ResultCount,
                UnresolvedTaskCount = item.UnresolvedTaskCount,
                TotalTaskCount = item.TotalTaskCount,
                AnomalyCount30Days = item.AnomalyCount30Days,
                AnomalySparkline = item.AnomalySparkline,
                IsActive = true,
                CreateTasks = item.CreateTasks,
                StoreResults = item.StoreResults,
                HasAnomalyDetection = item.HasAnomalyDetection,
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

        // Get all active subscriptions with their execution history
        var subscriptions = await context.Subscriptions
            .Where(s => s.ArchivedTime == null)
            .Select(s => new
            {
                s.Id,
                Executions = s.QueryExecutionHistory!
                    .Where(h => h.CreatedTime >= thirtyDaysAgo)
                    .Select(h => h.NotificationStatus)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var healthyCount = 0;
        var warningCount = 0;
        var criticalCount = 0;
        var totalSuccessful = 0;
        var totalExecutions = 0;

        foreach (var subscription in subscriptions)
        {
            var executions = subscription.Executions.Count;
            var successful = CountSuccessfulExecutions(subscription.Executions);

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
            TotalSubscriptions = subscriptions.Count,
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

    private static int CountSuccessfulExecutions(IEnumerable<dynamic> executions)
    {
        return executions.Count(e =>
            e.NotificationStatus == NotificationStatus.NotificationSent ||
            e.NotificationStatus == NotificationStatus.NoResults ||
            e.NotificationStatus == NotificationStatus.NotificationSilenced);
    }
}

public interface IControlTowerService
{
    Task<ControlTowerHealthListData> GetSubscriptionHealthOverview(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken);

    Task<ControlTowerStatistics> GetControlTowerStatistics(CancellationToken cancellationToken);
}
