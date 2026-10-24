using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// The home dashboards aggregate in SQL (execution durations per day, runs per hour and status, per-subscription averages)
/// instead of reading every run. The grouped shapes must translate on both providers, and the notification window filters on the
/// indexed sent time.
/// </summary>
[TestFixture]
public class HomeAggregationTranslationTests
{
    [TestCase(false)]
    [TestCase(true)]
    public void ExecutionDurationDistribution_GroupsByDayAndDuration_Translates(bool sqlServer)
    {
        using var context = Create(sqlServer);
        var windowStart = DateTime.UtcNow.AddDays(-30);

        var sql = context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= windowStart)
            .GroupBy(x =>
                new
                {
                    x.CreatedTime.Date,
                    x.ExecutionTimeMs
                })
            .Select(x =>
                new
                {
                    x.Key.Date,
                    Ms = x.Key.ExecutionTimeMs,
                    Count = x.Count()
                })
            .ToQueryString();

        sql.Should().ContainEquivalentOf("GROUP BY").And.ContainEquivalentOf("COUNT(*)");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void PerSubscriptionAverage_Translates(bool sqlServer)
    {
        using var context = Create(sqlServer);
        var windowStart = DateTime.UtcNow.AddDays(-30);

        var perSubscription = context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= windowStart)
            .GroupBy(x => x.SubscriptionId)
            .Select(x =>
                new
                {
                    SubscriptionId = x.Key,
                    AvgMs = x.Average(y => y.ExecutionTimeMs)
                })
            .ToQueryString();

        perSubscription.Should().ContainEquivalentOf("AVG(");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void RunsPerHourAndStatus_GroupsByHourPart_Translates(bool sqlServer)
    {
        using var context = Create(sqlServer);
        var start = DateTime.UtcNow.AddHours(-24);

        var sql = context.QueryExecutionHistory
            .Where(x => x.CreatedTime >= start)
            .GroupBy(x =>
                new
                {
                    x.CreatedTime.Date,
                    x.CreatedTime.Hour,
                    x.NotificationStatus
                })
            .Select(x =>
                new
                {
                    x.Key.Date,
                    x.Key.Hour,
                    x.Key.NotificationStatus
                })
            .ToQueryString();

        sql.Should().ContainEquivalentOf("GROUP BY");
    }

    [TestCase(false, "sent_at")]
    [TestCase(true, "[SentAt]")]
    public void NotificationsPerDay_FilterTheIndexedSentTime(bool sqlServer, string column)
    {
        using var context = Create(sqlServer);
        var windowStart = DateTime.UtcNow.AddDays(-30);

        var sql = context.Notifications
            .Where(x => x.SentAt >= windowStart)
            .GroupBy(x => x.SentAt.Date)
            .Select(x =>
                new
                {
                    Date = x.Key,
                    Count = x.Count()
                })
            .ToQueryString();
        var indexed = context.Model.FindEntityType(typeof(Beacon.Core.Data.Entities.Notification))!
            .GetIndexes()
            .Any(x => x.Properties.Select(y => y.Name).SequenceEqual(["SentAt"]));

        sql.Should().Contain(column);
        indexed.Should().BeTrue("the window filter has an index on the sent time");
    }

    [Test]
    public void UtcDayBucket_ConvertsToUtcBeforeTruncating_OnPostgreSql()
    {
        using var context = Create(false);

        var sql = context.QueryExecutionHistory
            .GroupBy(x => UtcBuckets.Day(x.CreatedTime))
            .Select(x => new { Date = x.Key, Count = x.Count() })
            .ToQueryString();

        sql.Should().Contain("date_trunc('day', timezone('UTC', ");
    }

    [Test]
    public void UtcHourBucket_ConvertsToUtcBeforeTruncating_OnPostgreSql()
    {
        using var context = Create(false);

        var sql = context.QueryExecutionHistory
            .GroupBy(x => UtcBuckets.Hour(x.CreatedTime))
            .Select(x => new { Hour = x.Key, Count = x.Count() })
            .ToQueryString();

        sql.Should().Contain("date_trunc('hour', timezone('UTC', ");
    }

    [Test]
    public void UtcBuckets_OnSqlServer_TruncateTheZonelessColumn()
    {
        using var context = Create(true);

        var sql = context.QueryExecutionHistory
            .GroupBy(x => new { Day = UtcBuckets.Day(x.CreatedTime), Hour = UtcBuckets.Hour(x.CreatedTime) })
            .Select(x => new { x.Key.Day, x.Key.Hour, Count = x.Count() })
            .ToQueryString();

        sql.Should().Contain("DATEADD(hour, DATEDIFF(hour, 0, ").And.Contain("CONVERT(date, ");
    }

    [TestCase(false)]
    [TestCase(true)]
    public void DurationHistogram_RoundsTheDurationBeforeGrouping(bool sqlServer)
    {
        using var context = Create(sqlServer);

        var sql = context.QueryExecutionHistory
            .GroupBy(x => new { Date = UtcBuckets.Day(x.CreatedTime), Ms = Math.Round(x.ExecutionTimeMs) })
            .Select(x => new { x.Key.Date, x.Key.Ms, Count = x.Count(), TotalMs = x.Sum(y => y.ExecutionTimeMs) })
            .ToQueryString();

        sql.Should().ContainEquivalentOf("ROUND(").And.ContainEquivalentOf("GROUP BY");
    }

    private static BeaconContext Create(bool sqlServer) =>
        sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
}
