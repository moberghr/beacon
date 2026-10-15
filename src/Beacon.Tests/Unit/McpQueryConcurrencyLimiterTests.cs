using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="McpQueryConcurrencyLimiter"/> enforces <c>MaxConcurrentQueriesPerKey</c>: up to the limit per credential,
/// refused (never queued) past it, a slot freed exactly once when its lease is disposed, credentials counted apart.
/// </summary>
[TestFixture]
public class McpQueryConcurrencyLimiterTests
{
    [Test]
    public void UpToTheLimit_EachCallGetsASlot_AndTheNextIsRefused()
    {
        var limiter = new McpQueryConcurrencyLimiter();

        using var first = limiter.TryAcquire("api-key:1", 2);
        using var second = limiter.TryAcquire("api-key:1", 2);
        var third = limiter.TryAcquire("api-key:1", 2);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        third.Should().BeNull();
        limiter.InFlight("api-key:1").Should().Be(2);
    }

    [Test]
    public void DisposingALease_FreesItsSlotOnce()
    {
        var limiter = new McpQueryConcurrencyLimiter();
        var lease = limiter.TryAcquire("api-key:1", 1)!;
        using var other = limiter.TryAcquire("api-key:2", 1);

        lease.Dispose();
        lease.Dispose();

        limiter.InFlight("api-key:1").Should().Be(0);
        limiter.InFlight("api-key:2").Should().Be(1, "a second Dispose must not free another credential's or another call's slot");
        limiter.TryAcquire("api-key:1", 1).Should().NotBeNull();
    }

    [Test]
    public void Credentials_AreCountedApart()
    {
        var limiter = new McpQueryConcurrencyLimiter();
        using var full = limiter.TryAcquire("api-key:1", 1);

        using var other = limiter.TryAcquire("api-key:2", 1);

        limiter.TryAcquire("api-key:1", 1).Should().BeNull();
        other.Should().NotBeNull();
    }

    // The limit is the caller's resolved setting for the call: a project with a higher limit admits more.
    [Test]
    public void TheLimit_IsTheOnePassedForTheCall()
    {
        var limiter = new McpQueryConcurrencyLimiter();
        using var first = limiter.TryAcquire("user:5", 1);

        limiter.TryAcquire("user:5", 1).Should().BeNull();
        using var underAHigherLimit = limiter.TryAcquire("user:5", 2);
        underAHigherLimit.Should().NotBeNull();
    }

    [TestCase(0)]
    [TestCase(-3)]
    public void ALimitBelowOne_AdmitsOneCall(int limit)
    {
        var limiter = new McpQueryConcurrencyLimiter();

        using var first = limiter.TryAcquire("user:5", limit);

        first.Should().NotBeNull();
        limiter.TryAcquire("user:5", limit).Should().BeNull();
    }

    [Test]
    public async Task ConcurrentCalls_NeverExceedTheLimit_AndAllSlotsAreFreedAfterwards()
    {
        var limiter = new McpQueryConcurrencyLimiter();
        var peak = 0;
        var admitted = 0;
        var gate = new object();

        var calls = Enumerable.Range(0, 64)
            .Select(_ => Task.Run(async () =>
            {
                using var lease = limiter.TryAcquire("api-key:9", 4);
                if (lease == null)
                {
                    return;
                }

                lock (gate)
                {
                    admitted++;
                    peak = Math.Max(peak, limiter.InFlight("api-key:9"));
                }

                await Task.Delay(5);
            }))
            .ToList();
        await Task.WhenAll(calls);

        admitted.Should().BeGreaterThan(0);
        peak.Should().BeLessThanOrEqualTo(4);
        limiter.InFlight("api-key:9").Should().Be(0);
    }

    [Test]
    public void CallerKey_IsTheApiKey_ThenTheCallerHash_ThenTheUser()
    {
        McpQueryConcurrencyLimiter.CallerKeyOf(new McpProjectContext { ApiKeyId = 3, CallerHash = "h", UserId = 7 }).Should().Be("api-key:3");
        McpQueryConcurrencyLimiter.CallerKeyOf(new McpProjectContext { CallerHash = "h", UserId = 7 }).Should().Be("caller:h");
        McpQueryConcurrencyLimiter.CallerKeyOf(new McpProjectContext { UserId = 7 }).Should().Be("user:7");
        McpQueryConcurrencyLimiter.CallerKeyOf(new McpProjectContext()).Should().Be(McpQueryConcurrencyLimiter.AnonymousKey);
    }
}
