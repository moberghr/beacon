using FluentAssertions;
using NUnit.Framework;
using Beacon.AI.Services.Knowledge;

namespace Beacon.Tests.Unit;

/// <summary>
/// Unit coverage for the B5 Reciprocal Rank Fusion helper that fuses the lexical and dense
/// retrieval arms in <c>KnowledgeGraphService.SearchAsync</c>. Locks the RRF contract:
/// consensus (an item ranked in multiple lists) beats a single-list top hit, the constant k
/// controls how strongly a single top rank counts, keys de-duplicate, and empty inputs are safe.
/// </summary>
[TestFixture]
public class RrfFusionTests
{
    private static readonly Func<string, string> Identity = x => x;

    [Test]
    public void Fuse_ItemInBothLists_BeatsBetterRankedItemInOnlyOneList()
    {
        // "single" is the #1 hit of list one but appears nowhere else; "shared" is only #2 in each
        // list but shows up in both. RRF should reward the consensus and rank "shared" first.
        var lexical = new List<string> { "single", "shared" };
        var dense = new List<string> { "other", "shared" };

        var fused = ReciprocalRankFusion.Fuse(
            new IReadOnlyList<string>[] { lexical, dense },
            Identity);

        fused[0].Should().Be("shared", "an item ranked in both arms outranks a single-arm top hit");
        fused.IndexOf("shared").Should().BeLessThan(fused.IndexOf("single"));
    }

    [Test]
    public void FuseWithScores_SumsReciprocalContributionsAcrossLists()
    {
        var lexical = new List<string> { "a", "shared" };
        var dense = new List<string> { "b", "shared" };

        var scored = ReciprocalRankFusion.FuseWithScores(
            new IReadOnlyList<string>[] { lexical, dense },
            Identity,
            k: 60);

        // "shared" is rank 2 (0-based index 1 -> 1-based rank 2) in both lists: 1/(60+2) + 1/(60+2).
        var shared = scored.Single(x => x.Item == "shared");
        shared.Score.Should().BeApproximately((1.0 / 62) + (1.0 / 62), 1e-9);

        // "a" is rank 1 in one list only: 1/(60+1).
        var single = scored.Single(x => x.Item == "a");
        single.Score.Should().BeApproximately(1.0 / 61, 1e-9);

        scored[0].Item.Should().Be("shared");
    }

    [Test]
    public void Fuse_DeduplicatesByKey()
    {
        var lexical = new List<string> { "a", "b" };
        var dense = new List<string> { "a", "c" };

        var fused = ReciprocalRankFusion.Fuse(
            new IReadOnlyList<string>[] { lexical, dense },
            Identity);

        fused.Should().HaveCount(3);
        fused.Should().OnlyHaveUniqueItems();
        fused.Should().BeEquivalentTo(new[] { "a", "b", "c" });
    }

    [Test]
    public void Fuse_KControlsWeightOfATopRankedSingleHit()
    {
        // "A" is a single-list #1 hit; "B" is a weak #4 hit in BOTH lists. With a tiny k the single
        // top hit dominates; with the standard k=60 the two weak-but-consensus hits win. The flip
        // proves k genuinely re-weights how much a lone top rank is worth.
        var lexical = new List<string> { "A", "x1", "x2", "B" };
        var dense = new List<string> { "y1", "y2", "y3", "B" };
        var lists = new IReadOnlyList<string>[] { lexical, dense };

        ReciprocalRankFusion.Fuse(lists, Identity, k: 1)[0]
            .Should().Be("A", "with k=1 a single top-ranked hit outweighs two weak consensus hits");

        ReciprocalRankFusion.Fuse(lists, Identity, k: 60)[0]
            .Should().Be("B", "with k=60 the consensus across both arms wins");
    }

    [Test]
    public void Fuse_AllEmptyLists_ReturnsEmpty()
    {
        ReciprocalRankFusion.Fuse(
            new IReadOnlyList<string>[] { [], [] },
            Identity)
            .Should().BeEmpty();

        ReciprocalRankFusion.Fuse(
            Array.Empty<IReadOnlyList<string>>(),
            Identity)
            .Should().BeEmpty();
    }

    [Test]
    public void Fuse_OneEmptyOneNonEmpty_ReturnsTheNonEmptyRankingUnchanged()
    {
        var dense = new List<string> { "a", "b", "c" };

        var fused = ReciprocalRankFusion.Fuse(
            new IReadOnlyList<string>[] { [], dense },
            Identity);

        fused.Should().Equal("a", "b", "c");
    }
}
