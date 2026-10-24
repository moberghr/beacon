using Beacon.AI.Services.Embeddings;
using Beacon.Core.Helpers;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

[TestFixture]
public class NearestEmbeddingSelectorTests
{
    private sealed record Row(int Id, byte[] Bytes);

    [Test]
    public async Task TopK_MatchesFullSortOfDecodedVectors()
    {
        var random = new Random(7);
        var query = RandomVector(random, 16);
        var rows = Enumerable.Range(0, 300)
            .Select(x => new Row(x, EmbeddingCodec.ToBytes(x % 17 == 0 ? RandomVector(random, 4) : RandomVector(random, 16))))
            .ToList();

        var expected = rows
            .Where(x => x.Bytes.Length == query.Length * sizeof(float))
            .Select(x => new { x.Id, Score = EmbeddingCodec.Cosine(query, EmbeddingCodec.FromBytes(x.Bytes)) })
            .OrderByDescending(x => x.Score)
            .Take(12)
            .Select(x => x.Id)
            .ToList();

        var actual = await NearestEmbeddingSelector.TopKAsync(ToAsync(rows), x => x.Bytes, query, 12, CancellationToken.None);

        actual.Select(x => x.Id).Should().Equal(expected);
    }

    [Test]
    public async Task TopK_EqualScores_KeepArrivalOrder()
    {
        var same = EmbeddingCodec.ToBytes([1f, 0f]);
        var rows = new[] { new Row(1, same), new Row(2, same), new Row(3, same), new Row(4, EmbeddingCodec.ToBytes([0f, 1f])) };

        var actual = await NearestEmbeddingSelector.TopKAsync(ToAsync(rows), x => x.Bytes, [1f, 0f], 2, CancellationToken.None);

        actual.Select(x => x.Id).Should().Equal(1, 2);
    }

    [Test]
    public async Task TopK_NonPositiveLimit_ReturnsNothing()
    {
        var rows = new[] { new Row(1, EmbeddingCodec.ToBytes([1f, 0f])) };

        var actual = await NearestEmbeddingSelector.TopKAsync(ToAsync(rows), x => x.Bytes, [1f, 0f], 0, CancellationToken.None);

        actual.Should().BeEmpty();
    }

    [Test]
    public void Cosine_StoredBytes_EqualsDecodedVectorResult()
    {
        var random = new Random(3);
        var a = RandomVector(random, 32);
        var b = RandomVector(random, 32);

        EmbeddingCodec.Cosine(a, EmbeddingCodec.ToBytes(b)).Should().Be(EmbeddingCodec.Cosine(a, b));
    }

    [Test]
    public void Cosine_StoredBytes_MismatchedLength_Throws()
    {
        var act = () => EmbeddingCodec.Cosine([1f, 2f], EmbeddingCodec.ToBytes([1f, 2f, 3f]));

        act.Should().Throw<ArgumentException>();
    }

    private static float[] RandomVector(Random random, int length) =>
        Enumerable.Range(0, length)
            .Select(_ => (float)(random.NextDouble() * 2 - 1))
            .ToArray();

    private static async IAsyncEnumerable<T> ToAsync<T>(IEnumerable<T> items)
    {
        foreach (var item in items)
        {
            yield return item;
        }

        await Task.CompletedTask;
    }
}
