using Beacon.Core.Configuration;
using Beacon.Core.Helpers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A subscription's schedule may not fire more often than <c>Beacon:Subscriptions:MinIntervalMinutes</c>: the shortest
/// gap between two runs counts, within a day and from one firing day to the next. A schedule that is not a five-field cron
/// expression is refused with a message saying so.
/// </summary>
[TestFixture]
public class SubscriptionScheduleTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 13, 37, 0, DateTimeKind.Utc);

    [TestCase("*/5 * * * *")]
    [TestCase("0,5,10 * * * *")]
    [TestCase("0 * * * *")]
    [TestCase("0 8 * * *")]
    [TestCase("@hourly")]
    [TestCase("0,58 0,23 * * 1")]
    [TestCase("30 9 1 * *")]
    [TestCase("58,3 * * * *")]
    public void AScheduleAtLeastFiveMinutesApart_IsAccepted(string cron)
    {
        var act = () => SubscriptionSchedule.EnsureAllowed(cron, 5, Now);

        act.Should().NotThrow();
    }

    [TestCase("* * * * *")]
    [TestCase("*/4 * * * *")]
    [TestCase("0,1 8 * * *")]
    [TestCase("0-10 * * * *")]
    [TestCase("0-4 * * * *")]
    [TestCase("3-59/4 * * * *")]
    [TestCase("0,58 0,23 * * *")]
    [TestCase("0,59 0,23 * * *")]
    [TestCase("@every_minute")]
    public void AScheduleThatFiresMoreOftenThanTheMinimum_IsRefused(string cron)
    {
        var act = () => SubscriptionSchedule.EnsureAllowed(cron, 5, Now);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*every 5 minutes*Beacon:Subscriptions:MinIntervalMinutes*");
    }

    // Two days that both fire, one minute apart across midnight: 23:59 on the 28th, 00:00 on the 29th.
    [Test]
    public void TheGapAcrossMidnight_BetweenTwoFiringDays_Counts()
    {
        var act = () => SubscriptionSchedule.EnsureAllowed("0,59 0,23 28,29 * *", 5, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    // From the last day of a month to the first of the next: 23:59 on 31 January, 00:00 on 1 February.
    [Test]
    public void TheGapAcrossTheEndOfAMonth_Counts()
    {
        var act = () => SubscriptionSchedule.EnsureAllowed("0,59 0,23 1,31 * *", 5, Now);

        act.Should().Throw<InvalidOperationException>();
    }

    // "58,3": 55 minutes within the hour, then 5 from :58 to :03 of the next hour — at the minimum of 5, under one of 6.
    [Test]
    public void TheGapAcrossTheHour_Counts()
    {
        var atFive = () => SubscriptionSchedule.EnsureAllowed("58,3 * * * *", 5, Now);
        var atSix = () => SubscriptionSchedule.EnsureAllowed("58,3 * * * *", 6, Now);

        atFive.Should().NotThrow();
        atSix.Should().Throw<InvalidOperationException>().WithMessage("*every 6 minutes*");
    }

    // A schedule that never fires (30 February), or fires once in years (29 February, once a year), has no short gap.
    [TestCase("0 0 31 2 *")]
    [TestCase("0 0 29 2 *")]
    [TestCase("@yearly")]
    public void AScheduleThatFiresRarelyOrNever_IsAccepted(string cron)
    {
        var act = () => SubscriptionSchedule.EnsureAllowed(cron, SubscriptionOptions.MaxAllowedMinIntervalMinutes, Now);

        act.Should().NotThrow();
    }

    // 28 and 29 February only on a Friday and a Saturday: in a row only in 2048, past any short search window.
    [Test]
    public void TwoFiringDaysInARow_YearsAway_Count()
    {
        var act = () => SubscriptionSchedule.EnsureAllowed("0,59 0,23 28,29 2 FRI,SAT", 5, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*every 5 minutes*");
    }

    // The slowest schedules to judge: times near both ends of the day on days never in a row, searched over 400 years.
    [TestCase("0,58 0,23 * * 1,3,5")]
    [TestCase("0,58 0,23 */2 * 2")]
    public void ASearchOverTheWholeCalendarCycle_IsFast(string cron)
    {
        SubscriptionSchedule.EnsureAllowed(cron, 5, Now);
        var elapsed = System.Diagnostics.Stopwatch.StartNew();

        SubscriptionSchedule.EnsureAllowed(cron, 5, Now);

        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Test]
    public void TheMinimum_IsTheConfiguredOne()
    {
        var everyMinute = () => SubscriptionSchedule.EnsureAllowed("* * * * *", 1, Now);
        var everyTen = () => SubscriptionSchedule.EnsureAllowed("*/10 * * * *", 15, Now);
        var everyFifteen = () => SubscriptionSchedule.EnsureAllowed("*/15 * * * *", 15, Now);

        everyMinute.Should().NotThrow();
        everyTen.Should().Throw<InvalidOperationException>().WithMessage("*every 15 minutes*");
        everyFifteen.Should().NotThrow();
        var hourlyAtSixty = () => SubscriptionSchedule.EnsureAllowed("0 * * * *", 60, Now);
        hourlyAtSixty.Should().NotThrow();
    }

    // Cron with a seconds field is not a schedule Beacon runs: refused as such, not with the parser's own error.
    [TestCase("*/30 * * * * *")]
    [TestCase("0 */10 * * * *")]
    [TestCase("not a cron")]
    [TestCase("")]
    public void AScheduleThatIsNotAFiveFieldCron_IsRefused(string cron)
    {
        var act = () => SubscriptionSchedule.EnsureAllowed(cron, 5, Now);

        act.Should().Throw<InvalidOperationException>().WithMessage("*five fields*");
    }
}
