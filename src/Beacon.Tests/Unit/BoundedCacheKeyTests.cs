using FluentAssertions;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;

namespace Beacon.Tests.Unit;

[TestFixture]
public class BoundedCacheKeyTests
{
    [Test]
    public async Task RefreshLocks_HoldNoEntryOnceReleased()
    {
        var locks = new KeyedRefreshLocks();

        for (var key = 1; key <= 1000; key++)
        {
            using var releaser = await locks.AcquireAsync(key, CancellationToken.None);
        }

        locks.Count.Should().Be(0);
    }

    [Test]
    public async Task RefreshLocks_SerializeTheSameKeyAndKeepItWhileWaiting()
    {
        var locks = new KeyedRefreshLocks();
        var first = await locks.AcquireAsync(7, CancellationToken.None);

        var second = locks.AcquireAsync(7, CancellationToken.None);
        await Task.Delay(50);

        second.IsCompleted.Should().BeFalse();
        locks.Count.Should().Be(1);

        first.Dispose();
        using var acquired = await second;

        locks.Count.Should().Be(1);
    }

    [Test]
    public async Task RefreshLocks_DoNotBlockOtherKeys()
    {
        var locks = new KeyedRefreshLocks();
        using var held = await locks.AcquireAsync(1, CancellationToken.None);

        var other = locks.AcquireAsync(2, CancellationToken.None);

        other.IsCompleted.Should().BeTrue();
        (await other).Dispose();
    }

    [Test]
    public async Task RefreshLocks_ACancelledWaitLeavesNoEntryBehind()
    {
        var locks = new KeyedRefreshLocks();
        var held = await locks.AcquireAsync(3, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();

        var waiting = locks.AcquireAsync(3, cancellation.Token);
        await cancellation.CancelAsync();
        var act = async () => await waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        held.Dispose();

        locks.Count.Should().Be(0);
    }

    [Test]
    public void StatisticsCacheKey_LengthDoesNotGrowWithTheKeyword()
    {
        var shortKey = ControlTowerService.StatisticsCacheKey(Request(new string('a', 10)));
        var longKey = ControlTowerService.StatisticsCacheKey(Request(new string('a', 16_000)));

        longKey.Length.Should().Be(shortKey.Length);
    }

    [Test]
    public void StatisticsCacheKey_DiffersByKeyword()
    {
        ControlTowerService.StatisticsCacheKey(Request("alpha"))
            .Should().NotBe(ControlTowerService.StatisticsCacheKey(Request("beta")));
        ControlTowerService.StatisticsCacheKey(Request("alpha"))
            .Should().Be(ControlTowerService.StatisticsCacheKey(Request("alpha")));
    }

    private static GetControlTowerDataRequest Request(string keyword)
    {
        return new GetControlTowerDataRequest
        {
            Readable = ProjectSet.All,
            SearchKeyword = keyword
        };
    }
}
