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
    /// Bucketizes query-execution durations per day into avg/p50/p95/p99.
    /// </summary>
    public static List<HomePerfBucket> BuildPerfBuckets(
        IReadOnlyList<(DateTime Date, double Ms)> execs,
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

    /// <summary>
    /// Linear-interpolation percentile over a pre-sorted ascending list.
    /// </summary>
    public static double Percentile(List<double> sorted, double p)
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
