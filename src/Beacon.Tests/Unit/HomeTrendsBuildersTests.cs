using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Handlers.Home;

namespace Beacon.Tests.Unit;

[TestFixture]
public class HomeTrendsBuildersTests
{
    [Test]
    public void PerfBuckets_FromCountedDurations_MatchThePercentilesOfEveryRun()
    {
        var now = new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc);
        var today = now.Date;
        // 10 runs of 100 ms, 5 of 200 ms, 4 of 400 ms, 1 of 1000 ms today; nothing yesterday.
        var counted = new List<(DateTime Date, double Ms, int Count)>
        {
            (today, 400, 4),
            (today, 100, 10),
            (today, 1000, 1),
            (today, 200, 5)
        };
        var everyRun = counted
            .SelectMany(x => Enumerable.Repeat(x.Ms, x.Count))
            .Order()
            .ToList();

        var buckets = HomeTrendsBuilders.BuildPerfBuckets(counted, 2, now);

        buckets.Should().HaveCount(2);
        buckets[0].Should().BeEquivalentTo(new HomePerfBucket("d-1", 0m, 0m, 0m, 0m), "a day without runs is empty");
        buckets[1].AvgMs.Should().Be(Math.Round((decimal)everyRun.Average(), 1));
        buckets[1].P50Ms.Should().Be(Math.Round((decimal)Interpolated(everyRun, 0.50), 1));
        buckets[1].P95Ms.Should().Be(Math.Round((decimal)Interpolated(everyRun, 0.95), 1));
        buckets[1].P99Ms.Should().Be(Math.Round((decimal)Interpolated(everyRun, 0.99), 1));
    }

    private static double Interpolated(List<double> sorted, double p)
    {
        var index = p * (sorted.Count - 1);
        var lo = (int)Math.Floor(index);
        var hi = (int)Math.Ceiling(index);

        return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
    }
}
