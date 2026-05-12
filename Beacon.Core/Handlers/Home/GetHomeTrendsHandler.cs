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

        var subscriptionsSpark = BuildDailyCounts(subDates, 14, now);

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

        // Sparkline: daily counts over last 14 days
        var execDates14 = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= now.AddDays(-14))
            .Select(x => x.CreatedTime.Date)
            .ToListAsync(cancellationToken);

        var queriesSpark = BuildDailyCounts(execDates14, 14, now);

        // 30-day trend for line chart
        var execDates30 = execsCurrent
            .Select(x => x.CreatedTime.Date)
            .ToList();

        var queryTrend30d = BuildDailyCounts(execDates30, days, now);

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

        var perfBuckets = BuildPerfBuckets(execsWithTime, days, now);

        // ── Notifications ──────────────────────────────────────────────
        var notificationsCurrent = await context.Notifications
            .Where(x => x.SentAt >= windowStart)
            .Select(x => x.SentAt)
            .ToListAsync(cancellationToken);

        var notificationsPrior = await context.Notifications
            .Where(x => x.SentAt >= priorStart)
            .Where(x => x.SentAt < windowStart)
            .CountAsync(cancellationToken);

        var notificationsSent30d = notificationsCurrent.Count;
        var notificationsDelta = notificationsSent30d - notificationsPrior;

        var notifDates14 = await context.Notifications
            .Where(x => x.SentAt >= now.AddDays(-14))
            .Select(x => x.SentAt.Date)
            .ToListAsync(cancellationToken);

        var notificationsSpark = BuildDailyCounts(notifDates14, 14, now);

        var notifDates30 = notificationsCurrent
            .Select(x => x.Date)
            .ToList();

        var notificationsTrend30d = BuildDailyCounts(notifDates30, days, now);

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

        var anomaliesSpark = BuildDailyCounts(anomaliesDates14, 14, now);

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

    private static List<int> BuildDailyCounts(List<DateTime> dates, int numDays, DateTime now)
    {
        var result = new List<int>(numDays);
        for (var i = numDays - 1; i >= 0; i--)
        {
            var day = now.AddDays(-i).Date;
            result.Add(dates.Count(d => d == day));
        }
        return result;
    }

    private static List<HomePerfBucket> BuildPerfBuckets(
        IReadOnlyList<(DateTime Date, double Ms)> execs, int numDays, DateTime now)
    {
        var result = new List<HomePerfBucket>(numDays);
        for (var i = numDays - 1; i >= 0; i--)
        {
            var day = now.AddDays(-i).Date;
            var label = $"d-{i}";
            var dayExecs = execs
                .Where(x => x.Date == day)
                .Select(x => x.Ms)
                .OrderBy(x => x)
                .ToList();

            if (dayExecs.Count == 0)
            {
                result.Add(new HomePerfBucket(label, 0m, 0m, 0m, 0m));
                continue;
            }

            var avg = (decimal)dayExecs.Average();
            var p50 = (decimal)Percentile(dayExecs, 0.50);
            var p95 = (decimal)Percentile(dayExecs, 0.95);
            var p99 = (decimal)Percentile(dayExecs, 0.99);
            result.Add(new HomePerfBucket(label, Math.Round(avg, 1), Math.Round(p50, 1), Math.Round(p95, 1), Math.Round(p99, 1)));
        }
        return result;
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0)
        {
            return 0;
        }

        var idx = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        if (lo == hi)
        {
            return sorted[lo];
        }

        return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
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
