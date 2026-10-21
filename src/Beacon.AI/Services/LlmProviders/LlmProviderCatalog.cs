namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The installed providers as <see cref="LlmProviderFactory"/> sees them, so the catalog and the factory never disagree.
/// </summary>
internal sealed class LlmProviderCatalog(LlmProviderFactory factory) : ILlmProviderCatalog
{
    public bool AiEnabled => true;

    public IReadOnlyList<LlmProviderInfo> Registered { get; } = factory.RegisteredProviders
        .Select(x => new LlmProviderInfo(x, factory.RequiresApiKey(x)))
        .ToList();
}
