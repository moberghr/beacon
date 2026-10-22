using Beacon.AI.Services.LlmProviders;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.LlmProviders;

/// <summary>
/// The slot pool resizes while slots are held: growing frees slots at once (first forgiving what a shrink still owed),
/// shrinking takes free slots out and keeps held ones as they come back, and free plus held slots always add up to the
/// size once the held ones are back.
/// </summary>
[TestFixture]
[CancelAfter(30_000)]
public class LlmSlotPoolTests
{
    [Test]
    public void GrowingAfterAShrinkThatWasStillOwed_ForgivesTheDebtFirst()
    {
        var pool = new LlmSlotPool(3);
        TakeAll(pool).Should().Be(3);

        pool.Resize(1);
        pool.Resize(2);
        ReturnAll(pool, 3);

        TakeAll(pool).Should().Be(2);
    }

    [Test]
    public void ShrinkAndGrowCycles_WhileSlotsAreHeld_EndAtExactlyTheLastSize()
    {
        var pool = new LlmSlotPool(4);
        pool.TryTake().Should().BeTrue();
        pool.TryTake().Should().BeTrue();

        foreach (var size in new[] { 1, 6, 2, 2, 5, 1, 3 })
        {
            pool.Resize(size);
        }

        ReturnAll(pool, 2);
        TakeAll(pool).Should().Be(3);
    }

    [Test]
    public void ResizingToTheSameSize_ChangesNothing()
    {
        var pool = new LlmSlotPool(3);
        pool.TryTake().Should().BeTrue();

        pool.Resize(3);
        pool.Return();

        TakeAll(pool).Should().Be(3);
    }

    [Test]
    public void GrowingWithFreeSlots_AddsTheDifference()
    {
        var pool = new LlmSlotPool(2);

        pool.Resize(5);

        TakeAll(pool).Should().Be(5);
    }

    [Test]
    public void ShrinkingTheBackgroundShare_KeepsTheHeldSlotsUntilTheyComeBack()
    {
        var background = new LlmSlotPool(4);
        TakeAll(background).Should().Be(4);

        background.Resize(1);
        background.Return();
        background.Return();
        background.Return();
        background.TryTake().Should().BeFalse("three of the four held slots came back and were kept, one is still held");
        background.Return();

        TakeAll(background).Should().Be(1);
    }

    [Test]
    public void AResizeBelowOne_IsRefused()
    {
        var pool = new LlmSlotPool(2);

        var act = () => pool.Resize(0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ResizesTakesAndReturnsInParallel_LeaveExactlyTheLastSize()
    {
        var pool = new LlmSlotPool(4);
        var held = 0;
        var maxSeenAboveLimit = 0;
        using var stop = new CancellationTokenSource();
        var workers = Enumerable.Range(0, 16)
            .Select(_ => Task.Run(async () =>
            {
                for (var i = 0; i < 200; i++)
                {
                    await pool.TakeAsync(CancellationToken.None);
                    var now = Interlocked.Increment(ref held);
                    if (now > 8)
                    {
                        Interlocked.Exchange(ref maxSeenAboveLimit, now);
                    }

                    await Task.Yield();
                    Interlocked.Decrement(ref held);
                    pool.Return();
                }
            }))
            .ToList();
        var resizer = Task.Run(async () =>
        {
            var random = new Random(7);
            while (!stop.IsCancellationRequested)
            {
                pool.Resize(random.Next(1, 9));
                await Task.Yield();
            }
        });

        await Task.WhenAll(workers);
        stop.Cancel();
        await resizer;
        pool.Resize(5);

        held.Should().Be(0);
        maxSeenAboveLimit.Should().Be(0, "no size above 8 was ever set, and a shrink only waits for held slots");
        TakeAll(pool).Should().Be(5, "free plus held slots add up to the size once every held slot is back");
    }

    private static int TakeAll(LlmSlotPool pool)
    {
        var taken = 0;
        while (pool.TryTake())
        {
            taken++;
        }

        return taken;
    }

    private static void ReturnAll(LlmSlotPool pool, int count)
    {
        for (var i = 0; i < count; i++)
        {
            pool.Return();
        }
    }
}
