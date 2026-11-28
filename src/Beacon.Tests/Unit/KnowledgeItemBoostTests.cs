using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;

namespace Beacon.Tests.Unit;

/// <summary>
/// The bounded rank boost as a pure reordering (<see cref="KnowledgeItemBoost"/>): net evidence
/// <c>(helpful - harmful) / (helpful + harmful + 4)</c>, none below 5 signals or 2 callers, sort key
/// <c>index - 2 * net</c>, stable, held to 2 places from each item's index. It never adds or removes an item.
/// </summary>
[TestFixture]
[SetCulture("")]
public class KnowledgeItemBoostTests
{
    [Test]
    public void Net_WithoutStats_IsZero()
    {
        KnowledgeItemBoost.Net(null).Should().Be(0);
    }

    [TestCase(4, 2)]
    [TestCase(5, 1)]
    [TestCase(100, 1)]
    [TestCase(0, 0)]
    public void Net_BelowTheEvidenceFloor_IsZero(int signals, int callers)
    {
        KnowledgeItemBoost.Net(Evidence(1, helpful: 50, harmful: 0, signals, callers)).Should().Be(0);
    }

    [TestCase(6, 2, 4.0 / 12)]
    [TestCase(2, 6, -4.0 / 12)]
    [TestCase(4, 0, 0.5)]
    [TestCase(0, 0, 0.0)]
    [TestCase(3, 3, 0.0)]
    public void Net_AtTheEvidenceFloor_IsTheFormula(double helpful, double harmful, double expected)
    {
        KnowledgeItemBoost.Net(Evidence(1, helpful, harmful, signals: 5, callers: 2)).Should().BeApproximately(expected, 1e-12);
    }

    [TestCase(1e12, 0)]
    [TestCase(0, 1e12)]
    public void Net_HowEverStrongTheEvidence_StaysStrictlyBetweenMinusOneAndOne(double helpful, double harmful)
    {
        var net = KnowledgeItemBoost.Net(Evidence(1, helpful, harmful, signals: 1_000, callers: 500));

        net.Should().BeGreaterThan(-1).And.BeLessThan(1);
    }

    [TestCase(double.NaN, 0)]
    [TestCase(0, double.PositiveInfinity)]
    [TestCase(-10, 0)]
    [TestCase(0, -10)]
    public void Net_AScoreThatIsNotANonNegativeNumber_IsZero(double helpful, double harmful)
    {
        KnowledgeItemBoost.Net(Evidence(1, helpful, harmful, signals: 10, callers: 3)).Should().Be(0);
    }

    [Test]
    public void Reorder_WithoutEvidence_KeepsTheRetrievalOrder()
    {
        int[] items = [11, 12, 13, 14];

        KnowledgeItemBoost.Reorder(items, x => x, []).Should().Equal(11, 12, 13, 14);
    }

    // net 20 / 24 = 0.83: the key 2 - 1.67 = 0.33 passes the item at index 1, not the one at index 0.
    [Test]
    public void Reorder_HelpfulEvidence_MovesTheItemUp()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, [Evidence(13, helpful: 20, harmful: 0)]);

        ordered.Should().Equal(11, 13, 12);
    }

    [Test]
    public void Reorder_HarmfulEvidence_MovesTheItemDown()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, [Evidence(11, helpful: 0, harmful: 20)]);

        ordered.Should().Equal(12, 11, 13);
    }

    // Evidence too weak to cross half a place (net 2 / 8 = 0.25) leaves the order as it is.
    [Test]
    public void Reorder_WeakEvidence_DoesNotMoveTheItem()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, [Evidence(13, helpful: 3, harmful: 1)]);

        ordered.Should().Equal(11, 12, 13);
    }

    [Test]
    public void Reorder_EvidenceBelowTheFloor_DoesNotMoveTheItem()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(
            items,
            x => x,
            [Evidence(13, helpful: 500, harmful: 0, signals: 4, callers: 4), Evidence(12, helpful: 500, harmful: 0, signals: 40, callers: 1)]);

        ordered.Should().Equal(11, 12, 13);
    }

    // net 4 / 8 = 0.5 exactly: the key 1 - 1 = 0 ties with the item at index 0, which keeps its place.
    [Test]
    public void Reorder_EqualKeys_KeepTheRetrievalOrder()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, [Evidence(12, helpful: 4, harmful: 0)]);

        ordered.Should().Equal(11, 12, 13);
    }

    // The strongest evidence one item can have moves it at most 2 places (with the others neutral, one place: its key stays
    // more than index - 2), whatever its place.
    [Test]
    public void Reorder_OneItemsEvidence_MovesItAtMostTwoPlaces([Values(0, 1, 2, 3, 4, 5)] int index, [Values(true, false)] bool helpful)
    {
        var items = Enumerable.Range(100, 6).ToList();
        var boosted = items[index];
        var evidence = helpful
            ? Evidence(boosted, helpful: 1e9, harmful: 0)
            : Evidence(boosted, helpful: 0, harmful: 1e9);

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, [evidence]);

        Math.Abs(ordered.IndexOf(boosted) - index).Should().BeLessThanOrEqualTo(2);
        ordered.Should().BeEquivalentTo(items);
        var moved = helpful ? index > 0 : index < items.Count - 1;
        ordered.IndexOf(boosted).Should().Be(moved ? (helpful ? index - 1 : index + 1) : index);
    }

    // Two items moving toward each other each move 2 places: the helpful last one passes both, the harmful first one is passed.
    [Test]
    public void Reorder_TwoItemsMovingTowardEachOther_EachMoveTwoPlaces()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(
            items,
            x => x,
            [Evidence(11, helpful: 0, harmful: 40), Evidence(13, helpful: 40, harmful: 0)]);

        ordered.Should().Equal(13, 12, 11);
    }

    // Keys alone: 11 (harmful, ~2) is passed by 12 (1), 13 (helpful, ~0) and 14 (helpful, ~1): 3 places down. Held to 2,
    // it goes before 14, which then keeps its place.
    [Test]
    public void Reorder_AnItemTheKeysWouldMoveThreePlacesDown_IsHeldToTwo()
    {
        int[] items = [11, 12, 13, 14];

        var ordered = KnowledgeItemBoost.Reorder(
            items,
            x => x,
            [Evidence(11, helpful: 0, harmful: 1e6), Evidence(13, helpful: 1e6, harmful: 0), Evidence(14, helpful: 1e6, harmful: 0)]);

        ordered.Should().Equal(13, 12, 11, 14);
    }

    // Keys alone: 14 (helpful, ~1) passes 12 (harmful, 1 + 6/7), 11 (harmful, ~2) and 13 (2): 3 places up. Held to 2, the
    // first place goes to 12 and 14 takes the second.
    [Test]
    public void Reorder_AnItemTheKeysWouldMoveThreePlacesUp_IsHeldToTwo()
    {
        int[] items = [11, 12, 13, 14];

        var ordered = KnowledgeItemBoost.Reorder(
            items,
            x => x,
            [Evidence(11, helpful: 0, harmful: 1e6), Evidence(12, helpful: 0, harmful: 3), Evidence(14, helpful: 1e6, harmful: 0)]);

        ordered.Should().Equal(12, 14, 11, 13);
    }

    // Any evidence on distinct items: no item ends more than 2 places from its index, and when the formula's order keeps
    // that bound on its own it is the result.
    [Test]
    public void Reorder_AnyEvidence_MovesNoItemMoreThanTwoPlaces_AndKeepsTheFormulasOrderWithinTheBound([Range(1, 200)] int seed)
    {
        var random = new Random(seed);
        var items = Enumerable.Range(1, random.Next(1, 16)).ToList();
        var evidence = items
            .Where(x => random.Next(3) > 0)
            .Select(x => random.Next(2) == 0
                ? Evidence(x, random.NextDouble() * 1e3, random.NextDouble() * 5)
                : Evidence(x, random.NextDouble() * 5, random.NextDouble() * 1e3))
            .ToList();
        var netById = evidence.ToDictionary(x => x.ItemId, x => KnowledgeItemBoost.Net(x));
        var formulaOrder = items
            .Select((x, y) => (Item: x, Key: y - 2 * netById.GetValueOrDefault(x)))
            .OrderBy(x => x.Key)
            .Select(x => x.Item)
            .ToList();

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, evidence);

        ordered.Should().BeEquivalentTo(items);
        foreach (var item in items)
        {
            Math.Abs(ordered.IndexOf(item) - items.IndexOf(item)).Should().BeLessThanOrEqualTo(2);
        }

        var formulaKeepsTheBound = items.All(x => Math.Abs(formulaOrder.IndexOf(x) - items.IndexOf(x)) <= 2);
        if (formulaKeepsTheBound)
        {
            ordered.Should().Equal(formulaOrder);
        }
    }

    [Test]
    public void Reorder_EvidenceOfAnItemNotInTheList_IsIgnored()
    {
        int[] items = [11, 12, 13];

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, [Evidence(99, helpful: 0, harmful: 500)]);

        ordered.Should().Equal(11, 12, 13);
    }

    // Any evidence: the result holds exactly the items given (duplicates included), never one more or one less.
    [Test]
    public void Reorder_AnyEvidence_NeverAddsOrRemovesAnItem([Range(1, 40)] int seed)
    {
        var random = new Random(seed);
        var items = Enumerable.Range(0, random.Next(0, 12))
            .Select(x => random.Next(1, 8))
            .ToList();
        var evidence = Enumerable.Range(1, 8)
            .Where(x => random.Next(2) == 0)
            .Select(x => Evidence(x, random.NextDouble() * 50, random.NextDouble() * 50, random.Next(0, 12), random.Next(0, 4)))
            .ToList();

        var ordered = KnowledgeItemBoost.Reorder(items, x => x, evidence);

        ordered.Should().BeEquivalentTo(items);
        ordered.Should().HaveCount(items.Count);
    }

    [Test]
    public void Reorder_TheIdOfAnItem_ComesFromTheSelector()
    {
        (int Id, string Entry)[] items = [(5, "a"), (6, "b")];

        var ordered = KnowledgeItemBoost.Reorder(items, x => x.Id, [Evidence(6, helpful: 20, harmful: 0)]);

        ordered.Select(x => x.Entry).Should().Equal("b", "a");
    }

    private static KnowledgeItemEvidence Evidence(int itemId, double helpful, double harmful, int signals = 10, int callers = 3)
    {
        return new KnowledgeItemEvidence
        {
            ItemId = itemId,
            HelpfulScore = helpful,
            HarmfulScore = harmful,
            SignalCount = signals,
            CallerCount = callers
        };
    }
}
