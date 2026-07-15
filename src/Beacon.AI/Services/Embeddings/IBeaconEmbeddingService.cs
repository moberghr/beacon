namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// Abstraction over local, in-process text embedding generation (bge-small-en-v1.5, 384-dim).
/// Embeddings never leave the process — no schema text, column values, or questions are sent to any
/// external provider (§1.11 no-egress).
/// </summary>
/// <remarks>
/// Callers MUST check <see cref="IsAvailable"/> before calling <see cref="EmbedAsync"/> /
/// <see cref="EmbedBatchAsync"/> and fall back to lexical behaviour when it is <c>false</c>
/// (the model/tokenizer files may be absent or embeddings may be disabled by config). The embed
/// methods throw <see cref="System.InvalidOperationException"/> when the service is unavailable rather
/// than returning a degenerate vector.
/// </remarks>
public interface IBeaconEmbeddingService
{
    /// <summary>Dimensionality of the produced vectors (384 for bge-small-en-v1.5).</summary>
    int Dimensions { get; }

    /// <summary>
    /// True when embeddings are enabled by config AND the model + tokenizer files are present.
    /// Checking this property never loads the model and never throws.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Embeds a single text into an L2-normalized <see cref="Dimensions"/>-length vector.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct);

    /// <summary>Embeds a batch of texts, preserving input order.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct);
}
