namespace Beacon.AI.Services.Embeddings;

/// <summary>
/// App-level embedding configuration bound from <c>Beacon:Embeddings</c>. Model + tokenizer are
/// local assets (not secrets); embeddings are disabled by default so installs without a model file
/// keep working via the lexical fallback.
/// </summary>
internal sealed record EmbeddingOptions
{
    public bool Enabled { get; init; }

    public string? ModelPath { get; init; }

    public string? TokenizerPath { get; init; }
}
