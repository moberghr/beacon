using Beacon.AI.Services.Embeddings;

namespace Beacon.Tests.Common;

/// <summary>
/// Deterministic in-memory embedding service for unit/translation tests — no ONNX model required.
/// The same text always yields the same L2-normalized 384-dim vector; different texts yield different
/// vectors. Seeded from a process-stable FNV-1a hash (NOT <c>string.GetHashCode</c>, which is randomized
/// per process). Used by later batches' tests as the <see cref="IBeaconEmbeddingService"/> double.
/// </summary>
public sealed class FakeEmbeddingService : IBeaconEmbeddingService
{
    private const int Dim = 384;

    public int Dimensions => Dim;

    public bool IsAvailable => true;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct) =>
        Task.FromResult(Embed(text));

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<float[]>>(texts.Select(Embed).ToArray());

    private static float[] Embed(string text)
    {
        var random = new Random(StableSeed(text ?? string.Empty));
        var vector = new float[Dim];
        double sumSquares = 0;
        for (var i = 0; i < Dim; i++)
        {
            var value = (float)((random.NextDouble() * 2.0) - 1.0);
            vector[i] = value;
            sumSquares += (double)value * value;
        }

        var norm = Math.Sqrt(sumSquares);
        if (norm > 0)
        {
            for (var i = 0; i < Dim; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }

    private static int StableSeed(string text)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var c in text)
            {
                hash ^= c;
                hash *= 16777619u;
            }

            return (int)hash;
        }
    }
}
