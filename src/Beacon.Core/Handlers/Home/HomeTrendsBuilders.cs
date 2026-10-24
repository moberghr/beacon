namespace Beacon.Core.Handlers.Home;

/// <summary>
/// Pure-function helpers used to shape data for <see cref="GetHomeTrendsHandler"/>.
/// Kept side-effect-free so the handler stays focused on data loading.
/// </summary>
internal static class HomeTrendsBuilders
{
    /// <summary>
    /// Aligns a list of timestamps to per-day buckets across the last
    /// <paramref name="numDays"/> days, returning the bucket counts in
    /// chronological order (oldest → today).
    /// </summary>
    public static List<int> BuildDailyCounts(List<DateTime> dates, int numDays, DateTime now)
    {
        var result = new List<int>(numDays);
        for (var i = numDays - 1; i >= 0; i--)
        {
            var day = now.AddDays(-i).Date;
            result.Add(dates.Count(x => x == day));
        }
        return result;
    }

    /// <summary>
    /// Aligns pre-aggregated per-day counts to buckets across the last
    /// <paramref name="numDays"/> days, returning the bucket counts in
    /// chronological order (oldest → today).
    /// </summary>
    public static List<int> BuildDailyCounts(IReadOnlyDictionary<DateTime, int> countsByDay, int numDays, DateTime now)
    {
        var result = new List<int>(numDays);
        for (var i = numDays - 1; i >= 0; i--)
        {
            var day = now.AddDays(-i).Date;
            result.Add(countsByDay.GetValueOrDefault(day));
        }
        return result;
    }

    /// <summary>
    /// Bucketizes query-execution durations per day into avg/p50/p95/p99, from durations already counted per day (<c>Count</c> executions took <c>Ms</c>), so the
    /// percentiles are exact without one row per execution.
    /// </summary>
    public static List<HomePerfBucket> BuildPerfBuckets(
        IReadOnlyList<(DateTime Date, double Ms, int Count)> execs,
        int numDays,
        DateTime now)
    {
        var result = new List<HomePerfBucket>(numDays);
        for (var i = numDays - 1; i >= 0; i--)
        {
            var day = now.AddDays(-i).Date;
            var label = $"d-{i}";
            var dayExecs = execs
                .Where(x => x.Date == day)
                .OrderBy(x => x.Ms)
                .ToList();
            var total = dayExecs.Sum(x => x.Count);

            if (total == 0)
            {
                result.Add(new HomePerfBucket(label, 0m, 0m, 0m, 0m));
                continue;
            }

            var avg = (decimal)(dayExecs.Sum(x => x.Ms * x.Count) / total);
            var p50 = (decimal)Percentile(dayExecs, total, 0.50);
            var p95 = (decimal)Percentile(dayExecs, total, 0.95);
            var p99 = (decimal)Percentile(dayExecs, total, 0.99);
            result.Add(new HomePerfBucket(label, Math.Round(avg, 1), Math.Round(p50, 1), Math.Round(p95, 1), Math.Round(p99, 1)));
        }
        return result;
    }

    private static double Percentile(List<(DateTime Date, double Ms, int Count)> sorted, int total, double p)
    {
        var idx = p * (total - 1);
        var lo = (int)Math.Floor(idx);
        var hi = (int)Math.Ceiling(idx);
        var loValue = ValueAtRank(sorted, lo);
        if (lo == hi)
        {
            return loValue;
        }

        return loValue + (ValueAtRank(sorted, hi) - loValue) * (idx - lo);
    }

    private static double ValueAtRank(List<(DateTime Date, double Ms, int Count)> sorted, int rank)
    {
        var seen = 0;
        foreach (var group in sorted)
        {
            seen += group.Count;
            if (rank < seen)
            {
                return group.Ms;
            }
        }

        return sorted[^1].Ms;
    }
}
