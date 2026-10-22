using Beacon.Core.Configuration;
using Cronos;

namespace Beacon.Core.Helpers;

/// <summary>
/// Whether a subscription's cron schedule may be saved: a five-field cron expression whose runs are never closer than the
/// deployment's minimum interval (<see cref="SubscriptionOptions.MinIntervalMinutes"/>).
/// </summary>
internal static class SubscriptionSchedule
{
    // The Gregorian calendar repeats after 400 years: 146,097 days, a whole number of weeks.
    private const int GregorianCycleDays = 146_097;

    /// <summary>Refuses a schedule that is not a five-field cron expression, whatever its interval.</summary>
    public static void EnsureValid(string? cron)
    {
        Parse(cron);
    }

    public static void EnsureAllowed(string? cron, int minIntervalMinutes, DateTime nowUtc)
    {
        var expression = Parse(cron);
        if (FiresCloserThan(expression, TimeSpan.FromMinutes(minIntervalMinutes), nowUtc))
        {
            throw new InvalidOperationException(
                $"The schedule runs more often than every {minIntervalMinutes} minutes, the shortest interval this deployment allows ({SubscriptionOptions.SectionName}:{nameof(SubscriptionOptions.MinIntervalMinutes)}).");
        }
    }

    /// <summary>
    /// Whether two consecutive runs (in UTC, as the job runner fires them) are ever closer than <paramref name="minimum"/>
    /// (at most a day). A five-field schedule fires at the same times of day on every day it fires (only which days
    /// differ), so the gaps are those between one firing day's times, and — when two calendar days in a row both fire —
    /// the one from the last time of a day to the first of the next. The calendar repeats every 400 years (146,097 days,
    /// a whole number of weeks), so searching that span for two firing days in a row is exhaustive; it runs only when that
    /// gap would be too short.
    /// </summary>
    private static bool FiresCloserThan(CronExpression expression, TimeSpan minimum, DateTime fromUtc)
    {
        var day = DateTime.SpecifyKind(fromUtc.Date, DateTimeKind.Utc);
        var first = expression.GetNextOccurrence(day, inclusive: true);
        if (first == null)
        {
            return false;
        }

        var last = first.Value;
        var next = expression.GetNextOccurrence(last);
        while (next != null && next.Value.Date == first.Value.Date)
        {
            if (next.Value - last < minimum)
            {
                return true;
            }

            last = next.Value;
            next = expression.GetNextOccurrence(last);
        }

        var timesSpan = last - first.Value;
        if (TimeSpan.FromDays(1) - timesSpan >= minimum)
        {
            return false;
        }

        var end = first.Value.Date.AddDays(GregorianCycleDays + 1);
        while (next != null && next.Value < end)
        {
            if (next.Value.Date == last.Date.AddDays(1))
            {
                return true;
            }

            last = next.Value + timesSpan;
            next = expression.GetNextOccurrence(last);
        }

        return false;
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
