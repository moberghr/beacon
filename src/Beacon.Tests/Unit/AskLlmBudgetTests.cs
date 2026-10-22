using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Mcp;

namespace Beacon.Tests.Unit;

/// <summary>
/// <see cref="AskLlmBudget"/>: the per-<c>ask</c> count of LLM calls — a reserve kept by <c>TryConsume</c>, clamped grants
/// from <c>ConsumeUpTo</c>, and never more calls than the maximum, also under concurrent use.
/// </summary>
[TestFixture]
public class AskLlmBudgetTests
{
    [TestCase(0)]
    [TestCase(-1)]
    public void Constructor_NonPositiveMaximum_Throws(int maxCalls)
    {
        var act = () => new AskLlmBudget(maxCalls);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Test]
    public void NewBudget_HasEveryCallLeft()
    {
        var budget = new AskLlmBudget(3);

        budget.MaxCalls.Should().Be(3);
        budget.Used.Should().Be(0);
        budget.Remaining.Should().Be(3);
    }

    [Test]
    public void TryConsume_TakesCallsUntilNoneIsLeft()
    {
        var budget = new AskLlmBudget(2);

        budget.TryConsume().Should().BeTrue();
        budget.TryConsume().Should().BeTrue();
        budget.TryConsume().Should().BeFalse();
        budget.Used.Should().Be(2);
        budget.Remaining.Should().Be(0);
    }

    [TestCase(4, 3, true)]
    [TestCase(4, 4, false)]
    [TestCase(1, 0, true)]
    [TestCase(1, 1, false)]
    public void TryConsume_KeepsTheReserve(int maxCalls, int keepInReserve, bool expected)
    {
        var budget = new AskLlmBudget(maxCalls);

        budget.TryConsume(keepInReserve).Should().Be(expected);
        budget.Used.Should().Be(expected ? 1 : 0);
    }

    [TestCase(5, 3, 3)]
    [TestCase(5, 5, 5)]
    [TestCase(5, 9, 5)]
    [TestCase(5, 0, 0)]
    [TestCase(5, -2, 0)]
    public void ConsumeUpTo_GrantsWhatIsLeft_AndNothingForANonPositiveCount(int maxCalls, int requested, int granted)
    {
        var budget = new AskLlmBudget(maxCalls);

        budget.ConsumeUpTo(requested).Should().Be(granted);
        budget.Used.Should().Be(granted);
    }

    [Test]
    public void Consume_WithNoCallLeft_ThrowsAValueFreeBudgetError()
    {
        var budget = new AskLlmBudget(1);
        budget.Consume();

        var act = budget.Consume;

        act.Should().Throw<AskLlmBudgetExceededException>()
            .Which.Message.Should().Be("ask reached its limit of 1 LLM call before it could answer; narrow the question or ask again.");
        budget.Used.Should().Be(1);
    }

    [Test]
    public async Task ConcurrentConsumers_NeverTakeMoreThanTheMaximum()
    {
        var budget = new AskLlmBudget(50);
        var granted = 0;

        await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(x => Task.Run(() =>
            {
                var taken = x % 2 == 0
                    ? budget.TryConsume() ? 1 : 0
                    : budget.ConsumeUpTo(2);
                Interlocked.Add(ref granted, taken);
            })));

        granted.Should().Be(50);
        budget.Used.Should().Be(50);
        budget.Remaining.Should().Be(0);
    }
}
