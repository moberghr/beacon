using Beacon.AI.Services.LlmProviders;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The per-minute buckets: a new allowance keeps what is left, enabling starts full, an estimate never exceeds a minute's
/// allowance, a use above the estimate is repaid by the refill, a reservation taken from replaced buckets never touches
/// the new ones, background requests leave a quarter of each bucket, and waits are served in arrival order per lane.
/// </summary>
[TestFixture]
[CancelAfter(10_000)]
public class LlmRateLimiterTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);

    [Test]
    public void ANewAllowance_KeepsTheLevelLeft_CappedAtTheNewAllowance()
    {
        var limiter = Limiter(out _, requestsPerMinute: 10);
        Take(limiter, 6);

        limiter.Update(true, 20, 0);

        limiter.TryReserve(0, false).Should().NotBeNull("four requests were left");
        Take(limiter, 3);
        limiter.TryReserve(0, false).Should().BeNull("the larger allowance does not refill what was used");

        var lower = Limiter(out _, requestsPerMinute: 10);
        lower.Update(true, 2, 0);
        Take(lower, 2);
        lower.TryReserve(0, false).Should().BeNull("a full bucket is capped at the new allowance of two");
    }

    [Test]
    public void EnablingAfterDisabling_StartsWithAFullBucket()
    {
        var limiter = Limiter(out _, requestsPerMinute: 3);
        Take(limiter, 3);

        limiter.Update(false, 3, 0);
        limiter.Update(true, 3, 0);

        Take(limiter, 3);
        limiter.TryReserve(0, false).Should().BeNull();
    }

    [Test]
    public void AnEstimateAboveTheAllowance_ReservesAMinutesAllowance_SoItIsServed()
    {
        var limiter = Limiter(out _, tokensPerMinute: 1000);

        var reservation = limiter.TryReserve(50_000, false);

        reservation.Should().NotBeNull();
        reservation!.ReservedTokens.Should().Be(1000);
    }

    [Test]
    public void AUseAboveTheEstimate_LeavesADebtTheRefillRepays()
    {
        var limiter = Limiter(out var time, tokensPerMinute: 600);
        var reservation = limiter.TryReserve(100, false)!;

        limiter.Settle(reservation, 1000);

        reservation.Tokens!.Level.Should().Be(-400);
        limiter.TryReserve(100, false).Should().BeNull();
        time.Advance(TimeSpan.FromSeconds(49));
        limiter.TryReserve(100, false).Should().BeNull("500 tokens at 10 a second take 50 s");
        time.Advance(TimeSpan.FromSeconds(1));
        limiter.TryReserve(100, false).Should().NotBeNull();
    }

    [Test]
    public void SettlingOrRefundingAfterTheBucketsWereReplaced_LeavesTheNewBucketsAlone()
    {
        var limiter = Limiter(out _, requestsPerMinute: 2, tokensPerMinute: 1000);
        var settled = limiter.TryReserve(500, false)!;
        var refunded = limiter.TryReserve(500, false)!;

        limiter.Update(false, 0, 0);
        limiter.Update(true, 2, 1000);
        var fresh = limiter.TryReserve(1000, false)!;
        limiter.Settle(settled, 10);
        limiter.Refund(refunded);

        fresh.Tokens.Should().NotBeSameAs(settled.Tokens);
        fresh.Tokens!.Level.Should().Be(0);
        fresh.Requests!.Level.Should().Be(1);
    }

    [Test]
    public void TheBucketCountsAnAmountWithinTheToleranceAsThere()
    {
        var bucket = new LlmRateLimiter.Bucket(100, 0);
        bucket.Take(1e-7);

        bucket.TimeUntil(100).Should().Be(TimeSpan.Zero);

        bucket.Take(1e-5);
        bucket.TimeUntil(100).Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Test]
    public void ABackgroundRequest_LeavesAQuarterOfTheBucket_WhichAnInteractiveRequestMayUse()
    {
        var limiter = Limiter(out _, requestsPerMinute: 4);

        limiter.TryReserve(0, true).Should().NotBeNull();
        limiter.TryReserve(0, true).Should().NotBeNull();
        limiter.TryReserve(0, true).Should().NotBeNull();
        limiter.TryReserve(0, true).Should().BeNull("one request of four is left for interactive requests");

        limiter.TryReserve(0, false).Should().NotBeNull();
    }

    [Test]
    public void ABackgroundRequestLargerThanThreeQuarters_IsServedFromAFullBucket()
    {
        var limiter = Limiter(out _, tokensPerMinute: 1000);

        limiter.TryReserve(900, true).Should().NotBeNull();
    }

    [Test]
    public async Task ALargeRequestWaitingInLine_IsNotOvertakenBySmallerOnes()
    {
        var limiter = Limiter(out var time, tokensPerMinute: 600);
        limiter.TryReserve(500, false).Should().NotBeNull();
        var large = limiter.Join(false);
        limiter.TryReserve(large, 400, out var wait).Should().BeNull();
        wait.Should().Be(TimeSpan.FromSeconds(30));

        time.Advance(TimeSpan.FromSeconds(10));
        limiter.TryReserve(50, false).Should().BeNull("a smaller request does not pass the one waiting in line");
        var small = limiter.Join(false);
        limiter.TryReserve(small, 50, out var smallWait).Should().BeNull();
        smallWait.Should().Be(Timeout.InfiniteTimeSpan);

        var signalled = small.WaitAsync(Timeout.InfiniteTimeSpan, time, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(20));
        limiter.TryReserve(large, 400, out _).Should().NotBeNull();
        await signalled.WaitAsync(Guard);
        limiter.TryReserve(small, 50, out var next).Should().BeNull();
        next.Should().Be(TimeSpan.FromSeconds(5), "it is served next, once the bucket holds its 50 tokens");
    }

    [Test]
    public void AnInteractiveRequest_PassesBackgroundRequestsWaitingInLine_ButNotTheReverse()
    {
        var limiter = Limiter(out _, requestsPerMinute: 4);
        Take(limiter, 3);
        var background = limiter.Join(true);
        limiter.TryReserve(background, 0, out var backgroundWait).Should().BeNull("one of four is left, and one must stay");
        backgroundWait.Should().Be(TimeSpan.FromSeconds(15));

        limiter.TryReserve(0, false).Should().NotBeNull("an interactive request uses the reserve");
        var interactive = limiter.Join(false);
        limiter.TryReserve(0, true).Should().BeNull("a background request waits behind every earlier request");
        limiter.TryReserve(interactive, 0, out var interactiveWait).Should().BeNull();
        interactiveWait.Should().Be(TimeSpan.FromSeconds(15), "it is served ahead of the background request as soon as one request refills");
    }

    [Test]
    public void ARequestThatLeavesTheLine_NoLongerHoldsUpTheNext()
    {
        var limiter = Limiter(out _, requestsPerMinute: 1);
        Take(limiter, 1);
        var first = limiter.Join(false);
        var second = limiter.Join(false);
        limiter.TryReserve(second, 0, out var blocked).Should().BeNull();
        blocked.Should().Be(Timeout.InfiniteTimeSpan);

        limiter.Leave(first);

        limiter.TryReserve(second, 0, out var wait).Should().BeNull();
        wait.Should().Be(TimeSpan.FromSeconds(60));
    }

    private static LlmRateLimiter Limiter(out FakeTimeProvider time, int requestsPerMinute = 0, int tokensPerMinute = 0)
    {
        time = new FakeTimeProvider();
        var limiter = new LlmRateLimiter(time);
        limiter.Update(true, requestsPerMinute, tokensPerMinute);

        return limiter;
    }

    private static void Take(LlmRateLimiter limiter, int requests)
    {
        for (var i = 0; i < requests; i++)
        {
            limiter.TryReserve(0, false).Should().NotBeNull();
        }
    }
}
