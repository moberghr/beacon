using FluentAssertions;
using NUnit.Framework;
using Beacon.MCP.Services;

namespace Beacon.Tests.Unit;

/// <summary>
/// Value-grounding probes that run beside an MCP call take further slots of the caller's per-key count, so the statements
/// one credential has in flight stay within <c>MaxConcurrentQueriesPerKey</c>; a caller without a credential gets none.
/// </summary>
[TestFixture]
public class McpProbeConcurrencyLeasesTests
{
    [Test]
    public void AProbeSlot_CountsAgainstTheCallersCredential_UpToItsLimit()
    {
        var limiter = new McpQueryConcurrencyLimiter();
        var context = new McpProjectContext { ApiKeyId = 7 };
        using var call = limiter.TryAcquire(McpQueryConcurrencyLimiter.CallerKeyOf(context), 2);
        var leases = new McpProbeConcurrencyLeases(context, limiter);

        var first = leases.TryAcquire(2);
        var second = leases.TryAcquire(2);

        first.Should().NotBeNull();
        second.Should().BeNull("the call and one probe already hold both slots");
        limiter.InFlight("api-key:7").Should().Be(2);

        first!.Dispose();
        limiter.InFlight("api-key:7").Should().Be(1);
    }

    [Test]
    public void ACallerWithoutACredential_GetsNoProbeSlot()
    {
        var limiter = new McpQueryConcurrencyLimiter();
        var leases = new McpProbeConcurrencyLeases(new McpProjectContext(), limiter);

        leases.TryAcquire(3).Should().BeNull();
        limiter.InFlight(McpQueryConcurrencyLimiter.AnonymousKey).Should().Be(0);
    }
}
