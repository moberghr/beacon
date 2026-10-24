using Beacon.Core.Helpers;

namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// Picks the stored vectors most similar to a query vector while the rows stream past, keeping only the best
/// <c>limit</c> of them instead of decoding and sorting every row. Rows whose vector has another length are skipped;
/// equal scores keep the order the rows arrived in and a NaN score ranks last.
/// </summary>
internal static class NearestEmbeddingSelector
{
    public static async Task<List<T>> TopKAsync<T>(
        IAsyncEnumerable<T> rows, Func<T, byte[]> bytesOf, float[] queryVector, int limit, CancellationToken ct)
    {
        var best = new List<(T Row, double Score)>();
        if (limit <= 0)
        {
            return [];
        }

        var expectedBytes = queryVector.Length * sizeof(float);
        await foreach (var row in rows.WithCancellation(ct))
        {
            var bytes = bytesOf(row);
            if (bytes.Length != expectedBytes)
            {
                continue;
            }

            var score = EmbeddingCodec.Cosine(queryVector, bytes);
            if (double.IsNaN(score))
            {
                score = double.NegativeInfinity;
            }

            if (best.Count == limit && score <= best[^1].Score)
            {
                continue;
            }

            var index = best.Count;
            while (index > 0 && best[index - 1].Score < score)
            {
                index--;
            }

            best.Insert(index, (row, score));
            if (best.Count > limit)
            {
                best.RemoveAt(best.Count - 1);
            }
        }

        return best
            .Select(x => x.Row)
            .ToList();
    }
}
