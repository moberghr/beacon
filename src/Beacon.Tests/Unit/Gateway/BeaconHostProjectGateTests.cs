using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NUnit.Framework;
using Beacon.Core.HostData;

namespace Beacon.Tests.Unit.Gateway;

/// <summary>
/// The host project is synced the first time it is needed, once: concurrent askers share one sync, a failure backs off
/// (doubling, capped) and a later ask after the back-off tries again, and the sync never runs as the asking request's
/// caller.
/// </summary>
[TestFixture]
public class BeaconHostProjectGateTests
{
    private readonly FakeTimeProvider _time = new(DateTimeOffset.Parse("2026-10-21T10:00:00Z"));

    [Test]
    public async Task TheFirstAsk_SyncsTheHost_AndLaterOnesReuseTheProject()
    {
        var synchronizer = new FakeSynchronizer(_ => 7);
        using var gate = Gate(synchronizer);

        var first = await gate.GetProjectIdAsync(CancellationToken.None);
        var second = await gate.GetProjectIdAsync(CancellationToken.None);

        first.Should().Be(7);
        second.Should().Be(7);
        synchronizer.Calls.Should().Be(1);
        gate.State.Should().Be(new BeaconHostSyncState(true, 0, null, null));
    }

    [Test]
    public async Task ConcurrentAsks_ShareOneSync()
    {
        var release = new TaskCompletionSource();
        var synchronizer = new FakeSynchronizer(_ =>
        {
            release.Task.Wait();
            return 7;
        });
        using var gate = Gate(synchronizer);

        var asks = Enumerable.Range(0, 5).Select(_ => gate.GetProjectIdAsync(CancellationToken.None)).ToList();
        release.SetResult();
        var results = await Task.WhenAll(asks);

        results.Should().OnlyContain(x => x == 7);
        synchronizer.Calls.Should().Be(1);
    }

    [Test]
    public async Task AFailedSync_RefusesUntilItsBackOffPasses_ThenTriesAgain()
    {
        var synchronizer = new FakeSynchronizer(call => call == 1 ? throw new InvalidOperationException("database unreachable") : 7);
        using var gate = Gate(synchronizer);

        var failed = await gate.GetProjectIdAsync(CancellationToken.None);
        var backingOff = await gate.GetProjectIdAsync(CancellationToken.None);
        var state = gate.State;
        _time.Advance(TimeSpan.FromSeconds(5));
        var retried = await gate.GetProjectIdAsync(CancellationToken.None);

        failed.Should().BeNull();
        backingOff.Should().BeNull();
        state.Should().Be(new BeaconHostSyncState(false, 1, nameof(InvalidOperationException), _time.GetUtcNow()));
        retried.Should().Be(7);
        synchronizer.Calls.Should().Be(2, "nothing was tried while backing off");
    }

    [Test]
    public async Task ACancelledAsk_IsNotCountedAsAFailure_AndStartsNoBackOff()
    {
        using var cancelled = new CancellationTokenSource();
        var synchronizer = new FakeSynchronizer(call =>
        {
            // The asker gives up while the sync runs.
            if (call == 1)
            {
                cancelled.Cancel();
                cancelled.Token.ThrowIfCancellationRequested();
            }

            return 7;
        });
        using var gate = Gate(synchronizer);

        var ask = () => gate.GetProjectIdAsync(cancelled.Token);

        await ask.Should().ThrowAsync<OperationCanceledException>();
        gate.State.Should().Be(new BeaconHostSyncState(false, 0, null, null));
        (await gate.GetProjectIdAsync(CancellationToken.None)).Should().Be(7, "the next ask syncs at once, without waiting out a back-off");
    }

    [TestCase(1, 5)]
    [TestCase(2, 10)]
    [TestCase(3, 20)]
    [TestCase(7, 300)]
    [TestCase(1000, 300)]
    public void TheBackOff_DoublesFromTheFirstDelay_UpToTheCap(int failures, int seconds)
    {
        new BeaconHostSyncOptions().RetryDelay(failures).Should().Be(TimeSpan.FromSeconds(seconds));
    }

    [Test]
    public async Task TheSync_DoesNotRunInTheAskingRequestsContext()
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        HttpContext? seenBySync = new DefaultHttpContext();
        using var gate = Gate(new FakeSynchronizer(_ =>
        {
            seenBySync = accessor.HttpContext;
            return 7;
        }));

        await gate.GetProjectIdAsync(CancellationToken.None);

        seenBySync.Should().BeNull("the sync is Beacon's own work, never the caller of the request that asked");
        accessor.HttpContext.Should().NotBeNull("the asking request keeps its context");
    }

    private BeaconHostProjectGate Gate(IBeaconHostSynchronizer synchronizer) =>
        new(synchronizer, Options.Create(new BeaconHostSyncOptions { ProjectName = "inventhor" }), _time, NullLogger<BeaconHostProjectGate>.Instance);

    private sealed class FakeSynchronizer(Func<int, int> sync) : IBeaconHostSynchronizer
    {
        private int _calls;

        public int Calls => _calls;

        public Task<int> SyncAsync(string projectName, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);

            return Task.FromResult(sync(call));
        }
    }
}
