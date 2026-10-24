using Beacon.Core.Services.Embed;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit.Embed;

/// <summary>
/// The embed render caps: <c>MaxConcurrentRenders</c> over every grant, and a per-grant share (a quarter of the total unless
/// <c>MaxConcurrentRendersPerGrant</c> is set, at least 1) so one grant never holds every slot; a lease frees both.
/// </summary>
[TestFixture]
public class EmbedRenderLimiterTests
{
    [TestCase(8, null, 2)]
    [TestCase(3, null, 1)]
    [TestCase(1, null, 1)]
    [TestCase(8, 5, 5)]
    public void OneGrant_RunsAtMostItsShare(int total, int? perGrant, int expected)
    {
        using var limiter = Limiter(total, perGrant);

        var leases = Enumerable.Range(0, total)
            .Select(_ => limiter.TryAcquire(1))
            .ToList();

        leases.Count(x => x != null).Should().Be(expected);
        limiter.InFlight(1).Should().Be(expected);
    }

    [Test]
    public void TheTotal_HoldsOverEveryGrant()
    {
        using var limiter = Limiter(4, 2);
        using var a1 = limiter.TryAcquire(1);
        using var a2 = limiter.TryAcquire(1);
        using var b1 = limiter.TryAcquire(2);
        using var b2 = limiter.TryAcquire(2);

        limiter.TryAcquire(3).Should().BeNull("every slot is taken");
        limiter.InFlight(3).Should().Be(0, "a refused grant holds no share");
    }

    [Test]
    public void ALease_FreesItsSlotAndShare_Once()
    {
        using var limiter = Limiter(2, 1);
        var lease = limiter.TryAcquire(1)!;
        limiter.TryAcquire(1).Should().BeNull();

        lease.Dispose();
        lease.Dispose();

        limiter.InFlight(1).Should().Be(0);
        using var again = limiter.TryAcquire(1);
        using var other = limiter.TryAcquire(2);
        again.Should().NotBeNull();
        other.Should().NotBeNull();
        limiter.TryAcquire(3).Should().BeNull("a double dispose released one slot only");
    }

    private static EmbedRenderLimiter Limiter(int total, int? perGrant) =>
        new(Options.Create(new EmbedOptions { MaxConcurrentRenders = total, MaxConcurrentRendersPerGrant = perGrant }));
}
