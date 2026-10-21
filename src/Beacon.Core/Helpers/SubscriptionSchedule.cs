using Beacon.Core.Configuration;
using Cronos;

namespace Beacon.Core.Helpers;

/// <summary>
/// Whether a subscription's cron schedule may be saved: a five-field cron expression whose runs are never closer than the
/// deployment's minimum interval (<see cref="SubscriptionOptions.MinIntervalMinutes"/>).
/// </summary>
internal static class SubscriptionSchedule
{
    // Eight years of firing days: covers leap days and day-of-month/weekday combinations that meet only now and then.
    private const int DaysSearched = 8 * 366;

    /// <summary>Refuses a schedule that is not a five-field cron expression, whatever its interval.</summary>
    public static void EnsureValid(string? cron)
    {
        Parse(cron);
    }

    public static void EnsureAllowed(string? cron, int minIntervalMinutes, DateTime nowUtc)
    {
        var expression = Parse(cron);
        var shortest = ShortestInterval(expression, nowUtc);
        if (shortest < TimeSpan.FromMinutes(minIntervalMinutes))
        {
            throw new InvalidOperationException(
                $"The schedule runs more often than every {minIntervalMinutes} minutes, the shortest interval this deployment allows ({SubscriptionOptions.SectionName}:{nameof(SubscriptionOptions.MinIntervalMinutes)}).");
        }
    }

    /// <summary>
    /// The shortest time between two consecutive runs, in UTC, or null when the schedule fires at most once. A five-field
    /// schedule fires at the same times of day on every day it fires (only which days differ), so the gaps are those
    /// between one firing day's times and those from the last run of a firing day to the first of the next.
    /// </summary>
    private static TimeSpan? ShortestInterval(CronExpression expression, DateTime fromUtc)
    {
        var day = DateTime.SpecifyKind(fromUtc.Date, DateTimeKind.Utc);
        var first = expression.GetNextOccurrence(day, inclusive: true);
        if (first == null)
        {
            return null;
        }

        TimeSpan? shortest = null;
        var last = first.Value;
        var next = expression.GetNextOccurrence(last);
        while (next != null && next.Value.Date == first.Value.Date)
        {
            shortest = Shorter(shortest, next.Value - last);
            last = next.Value;
            next = expression.GetNextOccurrence(last);
        }

        var timesSpan = last - first.Value;
        var end = day.AddDays(DaysSearched);
        while (next != null && next.Value < end)
        {
            shortest = Shorter(shortest, next.Value - last);
            last = next.Value + timesSpan;
            next = expression.GetNextOccurrence(last);
        }

        return shortest;
    }

    private static TimeSpan Shorter(TimeSpan? current, TimeSpan candidate)
    {
        return current is { } x && x < candidate ? x : candidate;
    }

    private static CronExpression Parse(string? cron)
    {
        try
        {
            return CronExpression.Parse(cron ?? string.Empty);
        }
        catch (CronFormatException)
        {
            throw new InvalidOperationException(
                "The schedule is not a valid cron expression: use five fields (minute, hour, day of month, month, day of week), for example \"0 8 * * *\".");
        }
    }
}
