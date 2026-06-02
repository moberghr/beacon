using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Home;

internal sealed class GetHomeActivityHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetHomeActivityQuery, GetHomeActivityResult>
{
    public async Task<GetHomeActivityResult> Handle(GetHomeActivityQuery request, CancellationToken cancellationToken)
    {
        var limit = Math.Max(1, Math.Min(request.Limit, 50));

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var since = DateTime.UtcNow.AddDays(-30);

        // Recent successful executions (ok tone)
        var successExecs = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= since)
            .Where(x => x.NotificationStatus == NotificationStatus.NotificationSent)
            .OrderByDescending(x => x.CreatedTime)
            .Take(limit)
            .Select(x =>
                new HomeActivityItem(
                    "ok",
                    "Check",
                    x.Subscription.Query.Name + " ran successfully",
                    "scheduled · " + (int)x.ExecutionTimeMs + "ms",
                    x.CreatedTime))
            .ToListAsync(cancellationToken);

        // Recent failed executions (crit tone)
        var failedExecs = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= since)
            .Where(x => x.NotificationStatus == NotificationStatus.Failed || x.NotificationStatus == NotificationStatus.Timeout)
            .OrderByDescending(x => x.CreatedTime)
            .Take(limit)
            .Select(x =>
                new HomeActivityItem(
                    "crit",
                    "Alert",
                    x.Subscription.Query.Name + " failed",
                    x.Comment ?? (x.NotificationStatus == NotificationStatus.Timeout ? "execution timed out" : "execution failed"),
                    x.CreatedTime))
            .ToListAsync(cancellationToken);

        // New subscriptions (info tone). Global query filter excludes archived rows.
        var newSubs = await context.Subscriptions
            .Where(x => x.CreatedTime >= since)
            .OrderByDescending(x => x.CreatedTime)
            .Take(limit)
            .Select(x =>
                new HomeActivityItem(
                    "info",
                    "Plus",
                    "subscription created for " + x.Query.Name,
                    x.Recipients.Count + " recipient(s)",
                    x.CreatedTime))
            .ToListAsync(cancellationToken);

        // Resolved tasks (ok tone). Global query filter excludes archived rows.
        var resolvedTasks = await context.QueryTasks
            .Where(x => x.Resolved)
            .Where(x => x.ResolvedAt >= since)
            .OrderByDescending(x => x.ResolvedAt)
            .Take(limit)
            .Select(x =>
                new HomeActivityItem(
                    "ok",
                    "Check",
                    "task resolved for " + x.Subscription.Query.Name,
                    x.ResolutionNotes ?? "resolved",
                    x.ResolvedAt!.Value))
            .ToListAsync(cancellationToken);

        // Unresolved / new tasks (warn tone). Global query filter excludes archived rows.
        var openTasks = await context.QueryTasks
            .Where(x => !x.Resolved)
            .Where(x => x.CreatedTime >= since)
            .OrderByDescending(x => x.CreatedTime)
            .Take(limit)
            .Select(x =>
                new HomeActivityItem(
                    "warn",
                    "Alert",
                    "new task opened for " + x.Subscription.Query.Name,
                    null,
                    x.CreatedTime))
            .ToListAsync(cancellationToken);

        // Detected anomalies (warn/crit tone based on severity)
        var anomalies = await context.AnomalyEvents
            .Where(x => x.DetectedTime >= since)
            .OrderByDescending(x => x.DetectedTime)
            .Take(limit)
            .Select(x =>
                new HomeActivityItem(
                    x.Severity == "Critical" || x.Severity == "High" ? "crit" : "warn",
                    "Alert",
                    "anomaly detected on " + x.Subscription.Query.Name,
                    x.Explanation ?? ("severity: " + x.Severity),
                    x.DetectedTime))
            .ToListAsync(cancellationToken);

        // Merge all streams, sort by timestamp desc, take top N
        var items = successExecs
            .Concat(failedExecs)
            .Concat(newSubs)
            .Concat(resolvedTasks)
            .Concat(openTasks)
            .Concat(anomalies)
            .OrderByDescending(x => x.Timestamp)
            .Take(limit)
            .ToList();

        return new GetHomeActivityResult(items);
    }
}

public record GetHomeActivityQuery(int Limit = 10) : IRequest<GetHomeActivityResult>;

public record GetHomeActivityResult(List<HomeActivityItem> Items);

public record HomeActivityItem(
    string Tone,
    string Icon,
    string Title,
    string? Meta,
    DateTime Timestamp
);
