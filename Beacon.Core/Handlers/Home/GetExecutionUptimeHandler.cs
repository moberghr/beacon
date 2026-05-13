using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Home;

/// <summary>
/// Per-hour execution status for the BeaconHero status rail.
/// 24 buckets, oldest → newest. Empty hour → "muted".
/// </summary>
internal sealed class GetExecutionUptimeHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetExecutionUptimeQuery, GetExecutionUptimeResult>
{
    public async Task<GetExecutionUptimeResult> Handle(GetExecutionUptimeQuery request, CancellationToken cancellationToken)
    {
        var hours = Math.Clamp(request.Hours, 1, 168);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var windowStart = now.AddHours(-hours);

        // Bucket boundaries: align to the top of the hour so each tick covers a full hour
        // ending at the listed boundary. Newest bucket = current hour (partial).
        var currentHourStart = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var firstBucketStart = currentHourStart.AddHours(-(hours - 1));

        var rows = await context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= firstBucketStart)
            .Select(x => new { x.CreatedTime, x.NotificationStatus })
            .ToListAsync(cancellationToken);

        var ticks = new string[hours];
        for (var i = 0; i < hours; i++)
        {
            ticks[i] = "muted";
        }

        foreach (var row in rows)
        {
            var hourIndex = (int)Math.Floor((row.CreatedTime - firstBucketStart).TotalHours);
            if (hourIndex < 0 || hourIndex >= hours)
            {
                continue;
            }

            var current = ticks[hourIndex];
            var classification = Classify(row.NotificationStatus);

            // crit dominates warn dominates ok dominates muted
            if (Rank(classification) > Rank(current))
            {
                ticks[hourIndex] = classification;
            }
        }

        return new GetExecutionUptimeResult(ticks);
    }

    private static string Classify(NotificationStatus status) => status switch
    {
        NotificationStatus.Failed => "crit",
        NotificationStatus.Timeout => "crit",
        // Everything else (Sent / Silenced / NoResults / BelowThreshold / Created) is a
        // healthy execution. The beacon ran; whether it alerted is a separate concern.
        _ => "ok",
    };

    private static int Rank(string tick) => tick switch
    {
        "crit" => 3,
        "warn" => 2,
        "ok" => 1,
        _ => 0,
    };
}

public record GetExecutionUptimeQuery(int Hours = 24) : IRequest<GetExecutionUptimeResult>;

public record GetExecutionUptimeResult(string[] Ticks);
