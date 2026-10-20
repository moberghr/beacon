using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// Default <see cref="IBeaconEmbeddingService"/> when no embedding implementation is registered.
/// Callers guard on <see cref="IsAvailable"/> and fall back to lexical retrieval. Local embeddings
/// come from the <c>Moberg.Beacon.AI.Embeddings.Onnx</c> package (<c>AddBeaconOnnxEmbeddings()</c>).
/// </summary>
internal sealed class DisabledEmbeddingService : IBeaconEmbeddingService
{
    private const int EmbeddingDimensions = 384;
    private const string Guidance = "Install the Moberg.Beacon.AI.Embeddings.Onnx package and call AddBeaconOnnxEmbeddings().";

    public DisabledEmbeddingService(IConfiguration configuration, ILogger<DisabledEmbeddingService> logger)
    {
        var enabled = configuration.GetSection("Beacon:Embeddings").GetValue<bool>("Enabled");
        if (enabled)
        {
            logger.LogWarning(
                "Beacon:Embeddings:Enabled is true but no embedding service is registered, so retrieval stays lexical. {Guidance}",
                Guidance);
        }
    }

    public int Dimensions => EmbeddingDimensions;

    public bool IsAvailable => false;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        throw new InvalidOperationException($"Embeddings unavailable. {Guidance}");
    }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        throw new InvalidOperationException($"Embeddings unavailable. {Guidance}");
    }
}
