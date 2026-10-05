using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Helpers;

namespace Beacon.Core.Services;

internal class ControlTowerService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMemoryCache memoryCache) : IControlTowerService
{
    private const string StatisticsCacheKeyPrefix = "ControlTower_Statistics";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);
    private const double GreenThreshold = 90;
    private const double AmberThreshold = 70;

    // Subscriptions created at least this many days ago with zero executions in the window are Stalled.
    // Newly-created subscriptions inside this grace window stay Green ("waiting for first run").
    private const int StalledGraceDays = 1;

    private static readonly NotificationStatus[] SuccessStatuses =
    {
        NotificationStatus.NotificationSent,
        NotificationStatus.NoResults,
        NotificationStatus.NotificationSilenced
    };

    public async Task<PagedList<ControlTowerSubscriptionHealthData>> GetSubscriptionHealthOverview(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var windowStart = DateTime.UtcNow.AddDays(-request.TimeRangeDays);
        var stalledCutoff = DateTime.UtcNow.AddDays(-StalledGraceDays);

        var statsQuery = BuildSubscriptionStatsQuery(context, request, windowStart);
        statsQuery = ApplyFilters(statsQuery, request, stalledCutoff);

        var paged = await ApplySort(statsQuery, request.SortCriteria())
            .ToPagedListPreservingOrderAsync(request, cancellationToken);
        var pagedSubscriptions = paged.Items;

        if (pagedSubscriptions.Count == 0)
        {
            return PagedList<ControlTowerSubscriptionHealthData>.Create([], paged.TotalCount, request.PageSizeOrDefault);
        }

        var pageIds = pagedSubscriptions.Select(x => x.Id).ToList();

        var lastExecutionBySubscription = await LoadLastExecutions(context, pageIds, cancellationToken);
        var sparklineBySubscription = await LoadAnomalySparklines(context, pageIds, windowStart, cancellationToken);
        var anomalyEnabledSet = await LoadAnomalyEnabledSet(context, pageIds, cancellationToken);

        var anomalyCountsBySubscription = sparklineBySubscription
            .ToDictionary(x => x.Key, x => x.Value.Sum(p => p.AnomalyCount));

        var healthData = pagedSubscriptions
            .Select(item =>
            {
                var failedExecutions = item.TotalExecutions - item.SuccessfulExecutions;
                var successRate = item.TotalExecutions > 0
                    ? (double)item.SuccessfulExecutions / item.TotalExecutions * 100
                    : 0;
                var healthStatus = CalculateHealthStatus(
                    item.SuccessfulExecutions,
                    item.TotalExecutions,
                    item.SubscriptionCreatedTime,
                    stalledCutoff);
                var hasLastExecution = lastExecutionBySubscription.TryGetValue(item.Id, out var lastExecution);

                return new ControlTowerSubscriptionHealthData
                {
                    SubscriptionId = item.Id,
                    QueryName = item.QueryName,
                    DataSourceName = null,
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
            })
            .ToList();

        return PagedList<ControlTowerSubscriptionHealthData>.Create(healthData, paged.TotalCount, request.PageSizeOrDefault);
    }

    public async Task<ControlTowerStatistics> GetControlTowerStatistics(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken)
    {
        var cacheKey = $"{StatisticsCacheKeyPrefix}:{request.TimeRangeDays}:{request.FolderId}:{request.DataSourceId}:{request.SearchKeyword}:{request.HasUnresolvedTasks}:{request.HealthStatus}";
        if (memoryCache.TryGetValue(cacheKey, out ControlTowerStatistics? cached))
        {
            return cached!;
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var windowStart = DateTime.UtcNow.AddDays(-request.TimeRangeDays);
        var stalledCutoff = DateTime.UtcNow.AddDays(-StalledGraceDays);

        var statsQuery = BuildSubscriptionStatsQuery(context, request, windowStart);
        statsQuery = ApplyFilters(statsQuery, request, stalledCutoff);

        var rows = await statsQuery
            .Select(x => new
            {
                x.Id,
                x.TotalExecutions,
                x.SuccessfulExecutions,
                x.SubscriptionCreatedTime,
                x.UnresolvedTaskCount
            })
            .ToListAsync(cancellationToken);

        var healthyCount = 0;
        var warningCount = 0;
        var criticalCount = 0;
        var stalledCount = 0;
        var totalSuccessful = 0;
        var totalExecutions = 0;
        var totalUnresolved = 0;

        foreach (var row in rows)
        {
            totalExecutions += row.TotalExecutions;
            totalSuccessful += row.SuccessfulExecutions;
            totalUnresolved += row.UnresolvedTaskCount;

            var status = CalculateHealthStatus(
                row.SuccessfulExecutions,
                row.TotalExecutions,
                row.SubscriptionCreatedTime,
                stalledCutoff);

            switch (status)
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
                case HealthStatus.Stalled:
                    stalledCount++;
                    break;
            }
        }

        // Anomalies counted against the (already-filtered) page set.
        var pageIds = rows.Select(x => x.Id).ToList();
        var anomaliesInWindow = pageIds.Count == 0
            ? 0
            : await context.AnomalyEvents
                .Where(x => pageIds.Contains(x.SubscriptionId))
                .Where(x => x.DetectedTime >= windowStart)
                .CountAsync(cancellationToken);

        var overallSuccessRate = totalExecutions > 0
            ? (double)totalSuccessful / totalExecutions * 100
            : 100;

        var statistics = new ControlTowerStatistics
        {
            TotalSubscriptions = rows.Count,
            HealthySubscriptions = healthyCount,
            WarningSubscriptions = warningCount,
            CriticalSubscriptions = criticalCount,
            StalledSubscriptions = stalledCount,
            TotalUnresolvedTasks = totalUnresolved,
            TotalAnomalies30Days = anomaliesInWindow,
            OverallSuccessRate = overallSuccessRate,
            TimeRangeDays = request.TimeRangeDays
        };

        memoryCache.Set(cacheKey, statistics, CacheDuration);
        return statistics;
    }

    public async Task<ControlTowerSubscriptionDetail?> GetSubscriptionDetail(
        int subscriptionId,
        int timeRangeDays,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var subscription = await context.Subscriptions
            .Where(x => x.Id == subscriptionId)
            .Select(x =>
                new
                {
                    x.Id,
                    QueryId = x.Query.Id,
                    QueryName = x.Query.Name,
                    FolderPath = x.Query.Folder != null ? x.Query.Folder.Path : null,
                    x.CronExpression
                })
            .FirstOrDefaultAsync(cancellationToken);

        if (subscription == null)
        {
            return null;
        }

        var windowStart = DateTime.UtcNow.AddDays(-timeRangeDays);

        var recentExecutions = await context.QueryExecutionHistory
            .Where(x => x.SubscriptionId == subscriptionId)
            .OrderByDescending(x => x.CreatedTime)
            .Take(20)
            .Select(x =>
                new ControlTowerExecutionItem
                {
                    ExecutionId = x.Id,
                    CreatedTime = x.CreatedTime,
                    NotificationStatus = x.NotificationStatus,
                    ResultCount = x.ResultCount,
                    ExecutionTimeMs = x.ExecutionTimeMs,
                    ErrorMessage = x.Comment
                })
            .ToListAsync(cancellationToken);

        var openTasks = await context.QueryTasks
            .Where(x => x.SubscriptionId == subscriptionId)
            .Where(x => !x.Resolved)
            .OrderByDescending(x => x.CreatedTime)
            .Take(20)
            .Select(x =>
                new ControlTowerOpenTask
                {
                    TaskId = x.Id,
                    CreatedTime = x.CreatedTime,
                    SnoozedUntil = x.SnoozedUntil,
                    LatestResultCount = x.LatestResultCount,
                    Priority = x.Priority,
                    AssigneeUserId = x.AssigneeUserId
                })
            .ToListAsync(cancellationToken);

        var recentAnomalies = await context.AnomalyEvents
            .Where(x => x.SubscriptionId == subscriptionId)
            .Where(x => x.DetectedTime >= windowStart)
            .OrderByDescending(x => x.DetectedTime)
            .Take(20)
            .Select(x =>
                new ControlTowerAnomaly
                {
                    AnomalyId = x.Id,
                    DetectedTime = x.DetectedTime,
                    Severity = x.Severity,
                    CurrentValue = x.CurrentValue,
                    Explanation = x.Explanation,
                    Acknowledged = x.Acknowledged
                })
            .ToListAsync(cancellationToken);

        return new ControlTowerSubscriptionDetail
        {
            SubscriptionId = subscription.Id,
            QueryId = subscription.QueryId,
            QueryName = subscription.QueryName,
            FolderPath = subscription.FolderPath,
            CronExpression = subscription.CronExpression,
            TimeRangeDays = timeRangeDays,
            RecentExecutions = recentExecutions,
            OpenTasks = openTasks,
            RecentAnomalies = recentAnomalies
        };
    }

    internal static IQueryable<SubscriptionStatsRow> BuildSubscriptionStatsQuery(
        BeaconContext context,
        GetControlTowerDataRequest request,
        DateTime windowStart)
    {
        var query = context.Subscriptions.AsQueryable();

        if (request.FolderId.HasValue)
        {
            query = query.Where(x => x.Query.FolderId == request.FolderId.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchKeyword))
        {
            query = query.Where(x => x.Query.Name.Contains(request.SearchKeyword));
        }

        return query.Select(x =>
            new SubscriptionStatsRow
            {
                Id = x.Id,
                SubscriptionCreatedTime = x.CreatedTime,
                QueryName = x.Query.Name,
                FolderPath = x.Query.Folder != null ? x.Query.Folder.Path : null,
                CreateTasks = x.CreateTasks,
                StoreResults = x.StoreResults,
                AiActorId = x.AiActorId,
                AiActorName = x.AiActor != null ? x.AiActor.Name : null,
                TotalExecutions = x.QueryExecutionHistory!.Count(y => y.CreatedTime >= windowStart),
                SuccessfulExecutions = x.QueryExecutionHistory!.Count(y =>
                    y.CreatedTime >= windowStart && SuccessStatuses.Contains(y.NotificationStatus)),
                UnresolvedTaskCount = context.QueryTasks.Count(y => y.SubscriptionId == x.Id && !y.Resolved),
                TotalTaskCount = context.QueryTasks.Count(y => y.SubscriptionId == x.Id)
            });
    }

    private static IQueryable<SubscriptionStatsRow> ApplyFilters(
        IQueryable<SubscriptionStatsRow> source,
        GetControlTowerDataRequest request,
        DateTime stalledCutoff)
    {
        if (request.HasUnresolvedTasks is true)
        {
            source = source.Where(x => x.UnresolvedTaskCount > 0);
        }

        if (!request.HealthStatus.HasValue)
        {
            return source;
        }

        return request.HealthStatus.Value switch
        {
            HealthStatus.Green => source.Where(x =>
                x.TotalExecutions > 0 &&
                (double)x.SuccessfulExecutions / x.TotalExecutions * 100 >= GreenThreshold),
            HealthStatus.Amber => source.Where(x =>
                x.TotalExecutions > 0 &&
                (double)x.SuccessfulExecutions / x.TotalExecutions * 100 >= AmberThreshold &&
                (double)x.SuccessfulExecutions / x.TotalExecutions * 100 < GreenThreshold),
            HealthStatus.Red => source.Where(x =>
                x.TotalExecutions > 0 &&
                (double)x.SuccessfulExecutions / x.TotalExecutions * 100 < AmberThreshold),
            HealthStatus.Stalled => source.Where(x =>
                x.TotalExecutions == 0 && x.SubscriptionCreatedTime <= stalledCutoff),
            _ => source
        };
    }

    // The grid's sortable columns are computed from the stats row (success rate is a ratio), so they are
    // mapped here rather than resolved by property name. Anomalies and last execution are joined in after
    // paging and cannot order the page. No usable column means worst first.
    internal static IOrderedQueryable<SubscriptionStatsRow> ApplySort(
        IQueryable<SubscriptionStatsRow> source,
        IReadOnlyList<SortCriterion> criteria)
    {
        var criterion = criteria.FirstOrDefault();
        var descending = criterion?.SortDirection == SortDirection.Descending;

        var ordered = criterion?.SortColumn.ToLowerInvariant() switch
        {
            "queryname" => descending
                ? source.OrderByDescending(x => x.QueryName)
                : source.OrderBy(x => x.QueryName),
            "successrate" => descending
                ? source.OrderByDescending(x => x.TotalExecutions == 0 ? 100.0 : (double)x.SuccessfulExecutions / x.TotalExecutions * 100)
                : source.OrderBy(x => x.TotalExecutions == 0 ? 100.0 : (double)x.SuccessfulExecutions / x.TotalExecutions * 100),
            "totalexecutions" => descending
                ? source.OrderByDescending(x => x.TotalExecutions)
                : source.OrderBy(x => x.TotalExecutions),
            "unresolvedtaskcount" => descending
                ? source.OrderByDescending(x => x.UnresolvedTaskCount)
                : source.OrderBy(x => x.UnresolvedTaskCount),
            _ => source
                .OrderByDescending(x => x.UnresolvedTaskCount)
                .ThenBy(x => x.TotalExecutions == 0 ? 100.0 : (double)x.SuccessfulExecutions / x.TotalExecutions * 100)
        };

        return ordered
            .ThenBy(x => x.QueryName)
            .ThenBy(x => x.Id);
    }

    private static async Task<Dictionary<int, (NotificationStatus NotificationStatus, DateTime CreatedTime, int ResultCount)>>
        LoadLastExecutions(BeaconContext context, List<int> pageIds, CancellationToken cancellationToken)
    {
        if (pageIds.Count == 0)
        {
            return [];
        }

        var rows = await context.QueryExecutionHistory
            .Where(x => pageIds.Contains(x.SubscriptionId))
            .GroupBy(x => x.SubscriptionId)
            .Select(g =>
                new
                {
                    SubscriptionId = g.Key,
                    NotificationStatus = g.OrderByDescending(y => y.CreatedTime).Select(y => y.NotificationStatus).First(),
                    CreatedTime = g.OrderByDescending(y => y.CreatedTime).Select(y => y.CreatedTime).First(),
                    ResultCount = g.OrderByDescending(y => y.CreatedTime).Select(y => y.ResultCount).First()
                })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            x => x.SubscriptionId,
            x => (x.NotificationStatus, x.CreatedTime, x.ResultCount));
    }

    private static async Task<Dictionary<int, List<AnomalySparklinePoint>>> LoadAnomalySparklines(
        BeaconContext context,
        List<int> pageIds,
        DateTime windowStart,
        CancellationToken cancellationToken)
    {
        if (pageIds.Count == 0)
        {
            return [];
        }

        var rows = await context.AnomalyEvents
            .Where(x => pageIds.Contains(x.SubscriptionId))
            .Where(x => x.DetectedTime >= windowStart)
            .GroupBy(x => new { x.SubscriptionId, Date = x.DetectedTime.Date })
            .Select(g => new { g.Key.SubscriptionId, g.Key.Date, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.SubscriptionId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(x => x.Date)
                    .Select(x => new AnomalySparklinePoint { Date = x.Date, AnomalyCount = x.Count })
                    .ToList());
    }

    private static async Task<HashSet<int>> LoadAnomalyEnabledSet(
        BeaconContext context,
        List<int> pageIds,
        CancellationToken cancellationToken)
    {
        if (pageIds.Count == 0)
        {
            return [];
        }

        var rows = await context.AnomalyConfigs
            .Where(x => pageIds.Contains(x.SubscriptionId))
            .Where(x => x.Enabled)
            .Select(x => x.SubscriptionId)
            .ToListAsync(cancellationToken);

        return rows.ToHashSet();
    }

    private static HealthStatus CalculateHealthStatus(
        int successCount,
        int totalExecutions,
        DateTime subscriptionCreatedTime,
        DateTime stalledCutoff)
    {
        if (totalExecutions == 0)
        {
            return subscriptionCreatedTime <= stalledCutoff ? HealthStatus.Stalled : HealthStatus.Green;
        }

        var successRate = (double)successCount / totalExecutions * 100;

        return successRate switch
        {
            >= GreenThreshold => HealthStatus.Green,
            >= AmberThreshold => HealthStatus.Amber,
            _ => HealthStatus.Red
        };
    }

    internal sealed class SubscriptionStatsRow
    {
        public int Id { get; init; }
        public DateTime SubscriptionCreatedTime { get; init; }
        public string QueryName { get; init; } = null!;
        public string? FolderPath { get; init; }
        public bool CreateTasks { get; init; }
        public bool StoreResults { get; init; }
        public int? AiActorId { get; init; }
        public string? AiActorName { get; init; }
        public int TotalExecutions { get; init; }
        public int SuccessfulExecutions { get; init; }
        public int UnresolvedTaskCount { get; init; }
        public int TotalTaskCount { get; init; }
    }
}

public interface IControlTowerService
{
    Task<PagedList<ControlTowerSubscriptionHealthData>> GetSubscriptionHealthOverview(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken);

    Task<ControlTowerStatistics> GetControlTowerStatistics(
        GetControlTowerDataRequest request,
        CancellationToken cancellationToken);

    Task<ControlTowerSubscriptionDetail?> GetSubscriptionDetail(
        int subscriptionId,
        int timeRangeDays,
        CancellationToken cancellationToken);
}
