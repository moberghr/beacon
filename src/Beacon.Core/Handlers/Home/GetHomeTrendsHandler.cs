using Beacon.Core.Data;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Home;

internal sealed class GetHomeTrendsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetHomeTrendsQuery, GetHomeTrendsResult>
{
    public async Task<GetHomeTrendsResult> Handle(GetHomeTrendsQuery request, CancellationToken cancellationToken)
    {
        // Clamp sub-day windows to 1 day server-side
        var days = Math.Max(1, request.Days);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var windowStart = now.AddDays(-days);
        var priorStart = now.AddDays(-days * 2);

        // ── Subscriptions ──────────────────────────────────────────────
        // Global query filter on ArchivableBaseEntity already excludes archived rows.
        var totalSubscriptions = await context.Subscriptions
            .CountAsync(cancellationToken);

        var subscriptionsDaysAgo = await context.Subscriptions
            .Where(x => x.CreatedTime <= windowStart)
            .CountAsync(cancellationToken);

        var subscriptionDelta = totalSubscriptions - subscriptionsDaysAgo;

        // Subscriptions created per day over the last 14 days (for sparkline)
        var subDates = await context.Subscriptions
            .Where(x => x.CreatedTime >= now.AddDays(-14))
            .Select(x => x.CreatedTime.Date)
            .ToListAsync(cancellationToken);

        var subscriptionsSpark = HomeTrendsBuilders.BuildDailyCounts(subDates, 14, now);

        // ── Query executions ───────────────────────────────────────────
        var execsCurrent = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= windowStart)
            .Select(x => new { x.CreatedTime, x.ExecutionTimeMs, x.NotificationStatus, SubscriptionId = x.SubscriptionId })
            .ToListAsync(cancellationToken);

        var execsPrior = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= priorStart)
            .Where(x => x.CreatedTime < windowStart)
            .CountAsync(cancellationToken);

        var queryExecutions30d = execsCurrent.Count;
        var queryExecutionsDeltaPct = execsPrior > 0
            ? Math.Round((decimal)(queryExecutions30d - execsPrior) / execsPrior * 100, 1)
            : 0m;

        // Sparkline: daily counts over last 14 days, aggregated server-side
        var execDaily14 = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= now.AddDays(-14))
            .GroupBy(x => x.CreatedTime.Date)
            .Select(x =>
                new
                {
                    Date = x.Key,
                    Count = x.Count()
                })
            .ToListAsync(cancellationToken);

        var queriesSpark = HomeTrendsBuilders.BuildDailyCounts(
            execDaily14.ToDictionary(x => x.Date, x => x.Count), 14, now);

        // 30-day trend for line chart
        var execDates30 = execsCurrent
            .Select(x => x.CreatedTime.Date)
            .ToList();

        var queryTrend30d = HomeTrendsBuilders.BuildDailyCounts(execDates30, days, now);

        // ── Performance metrics ────────────────────────────────────────
        var avgExecutionMs = execsCurrent.Count > 0
            ? (decimal)execsCurrent.Average(x => x.ExecutionTimeMs)
            : 0m;

        var priorExecs = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= priorStart)
            .Where(x => x.CreatedTime < windowStart)
            .Select(x => x.ExecutionTimeMs)
            .ToListAsync(cancellationToken);

        var priorAvg = priorExecs.Count > 0 ? (decimal)priorExecs.Average() : 0m;
        var avgExecutionDeltaPct = priorAvg > 0
            ? Math.Round((avgExecutionMs - priorAvg) / priorAvg * 100, 1)
            : 0m;

        // Per-subscription average execution times for fastest/slowest
        var subExecTimes = execsCurrent
            .GroupBy(x => x.SubscriptionId)
            .Select(g => new { SubscriptionId = g.Key, AvgMs = g.Average(x => x.ExecutionTimeMs) })
            .ToList();

        int? fastestSubId = null;
        decimal fastestMs = 0m;
        int? slowestSubId = null;
        decimal slowestMs = 0m;

        if (subExecTimes.Count > 0)
        {
            var fastest = subExecTimes.MinBy(x => x.AvgMs)!;
            var slowest = subExecTimes.MaxBy(x => x.AvgMs)!;
            fastestSubId = fastest.SubscriptionId;
            fastestMs = (decimal)fastest.AvgMs;
            slowestSubId = slowest.SubscriptionId;
            slowestMs = (decimal)slowest.AvgMs;
        }

        // Look up query names for fastest/slowest subscriptions
        string? fastestQueryName = null;
        decimal fastestQueryDeltaMs = 0m;
        string? slowestQueryName = null;
        decimal slowestQueryDeltaMs = 0m;

        if (fastestSubId.HasValue)
        {
            fastestQueryName = await context.Subscriptions
                .Where(x => x.Id == fastestSubId.Value)
                .Select(x => x.Query.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (slowestSubId.HasValue)
        {
            slowestQueryName = await context.Subscriptions
                .Where(x => x.Id == slowestSubId.Value)
                .Select(x => x.Query.Name)
                .FirstOrDefaultAsync(cancellationToken);
        }

        // ── Perf histogram: per-day buckets ───────────────────────────
        var execsWithTime = execsCurrent
            .Select(x => (Date: x.CreatedTime.Date, Ms: x.ExecutionTimeMs))
            .ToList();

        var perfBuckets = HomeTrendsBuilders.BuildPerfBuckets(execsWithTime, days, now);

        // ── Notifications ──────────────────────────────────────────────
        // Per-day counts aggregated server-side; the window total derives from them.
        var notifDaily = await context.Notifications
            .Where(x => x.SentAt >= windowStart)
            .GroupBy(x => x.SentAt.Date)
            .Select(x =>
                new
                {
                    Date = x.Key,
                    Count = x.Count()
                })
            .ToListAsync(cancellationToken);

        var notificationsPrior = await context.Notifications
            .Where(x => x.SentAt >= priorStart)
            .Where(x => x.SentAt < windowStart)
            .CountAsync(cancellationToken);

        var notificationsSent30d = notifDaily.Sum(x => x.Count);
        var notificationsDelta = notificationsSent30d - notificationsPrior;

        var notifDaily14 = await context.Notifications
            .Where(x => x.SentAt >= now.AddDays(-14))
            .GroupBy(x => x.SentAt.Date)
            .Select(x =>
                new
                {
                    Date = x.Key,
                    Count = x.Count()
                })
            .ToListAsync(cancellationToken);

        var notificationsSpark = HomeTrendsBuilders.BuildDailyCounts(
            notifDaily14.ToDictionary(x => x.Date, x => x.Count), 14, now);

        var notificationsTrend30d = HomeTrendsBuilders.BuildDailyCounts(
            notifDaily.ToDictionary(x => x.Date, x => x.Count), days, now);

        // ── Anomalies ──────────────────────────────────────────────────
        // Using AnomalyEvent: open = not Acknowledged, acknowledged = Acknowledged
        var anomaliesOpen = await context.AnomalyEvents
            .Where(x => !x.Acknowledged)
            .CountAsync(cancellationToken);

        var anomaliesAcknowledged = await context.AnomalyEvents
            .Where(x => x.Acknowledged)
            .Where(x => x.DetectedTime >= windowStart)
            .CountAsync(cancellationToken);

        var anomaliesPrior = await context.AnomalyEvents
            .Where(x => x.DetectedTime >= priorStart)
            .Where(x => x.DetectedTime < windowStart)
            .CountAsync(cancellationToken);

        var anomaliesCurrent = await context.AnomalyEvents
            .Where(x => x.DetectedTime >= windowStart)
            .Select(x => x.DetectedTime.Date)
            .ToListAsync(cancellationToken);

        var anomaliesDelta = anomaliesCurrent.Count - anomaliesPrior;

        var anomaliesDates14 = await context.AnomalyEvents
            .Where(x => x.DetectedTime >= now.AddDays(-14))
            .Select(x => x.DetectedTime.Date)
            .ToListAsync(cancellationToken);

        var anomaliesSpark = HomeTrendsBuilders.BuildDailyCounts(anomaliesDates14, 14, now);

        // ── System overview counts ─────────────────────────────────────
        // Global query filter on ArchivableBaseEntity already excludes archived rows.
        var dataSourcesOnline = await context.DataSources
            .CountAsync(cancellationToken);

        var recipientsCount = await context.Recipients
            .CountAsync(cancellationToken);

        // Integrations: count distinct NotificationType values among active recipients
        // (represents distinct channel types configured, e.g. Email, Webhook, Teams, etc.)
        var integrationsCount = await context.Recipients
            .Select(x => (int)x.NotificationType)
            .Distinct()
            .CountAsync(cancellationToken);

        return new GetHomeTrendsResult(
            TotalSubscriptions: totalSubscriptions,
            SubscriptionDelta: subscriptionDelta,
            QueryExecutions30d: queryExecutions30d,
            QueryExecutionsDelta: queryExecutionsDeltaPct,
            NotificationsSent30d: notificationsSent30d,
            NotificationsDelta: notificationsDelta,
            AnomaliesOpen: anomaliesOpen,
            AnomaliesAcknowledged: anomaliesAcknowledged,
            AnomaliesDelta: anomaliesDelta,
            AvgExecutionMs: Math.Round(avgExecutionMs, 1),
            AvgExecutionDeltaPct: avgExecutionDeltaPct,
            FastestQueryName: fastestQueryName,
            FastestQueryMs: Math.Round(fastestMs, 1),
            FastestQueryDeltaMs: fastestQueryDeltaMs,
            SlowestQueryName: slowestQueryName,
            SlowestQueryMs: Math.Round(slowestMs, 1),
            SlowestQueryDeltaMs: slowestQueryDeltaMs,
            DataSourcesOnline: dataSourcesOnline,
            RecipientsCount: recipientsCount,
            IntegrationsCount: integrationsCount,
            SubscriptionsSpark: subscriptionsSpark,
            QueriesSpark: queriesSpark,
            NotificationsSpark: notificationsSpark,
            AnomaliesSpark: anomaliesSpark,
            QueryTrend30d: queryTrend30d,
            NotificationsTrend30d: notificationsTrend30d,
            PerfBuckets: perfBuckets
        );
    }

}

public record GetHomeTrendsQuery(int Days = 30) : IRequest<GetHomeTrendsResult>;

public record GetHomeTrendsResult(
    int TotalSubscriptions,
    int SubscriptionDelta,
    int QueryExecutions30d,
    decimal QueryExecutionsDelta,
    int NotificationsSent30d,
    int NotificationsDelta,
    int AnomaliesOpen,
    int AnomaliesAcknowledged,
    int AnomaliesDelta,
    decimal AvgExecutionMs,
    decimal AvgExecutionDeltaPct,
    string? FastestQueryName,
    decimal FastestQueryMs,
    decimal FastestQueryDeltaMs,
    string? SlowestQueryName,
    decimal SlowestQueryMs,
    decimal SlowestQueryDeltaMs,
    int DataSourcesOnline,
    int RecipientsCount,
    int IntegrationsCount,
    List<int> SubscriptionsSpark,
    List<int> QueriesSpark,
    List<int> NotificationsSpark,
    List<int> AnomaliesSpark,
    List<int> QueryTrend30d,
    List<int> NotificationsTrend30d,
    List<HomePerfBucket> PerfBuckets
);

public record HomePerfBucket(string Label, decimal AvgMs, decimal P50Ms, decimal P95Ms, decimal P99Ms);
