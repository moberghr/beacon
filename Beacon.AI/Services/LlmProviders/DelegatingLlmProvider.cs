using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// A proxy ILlmProvider that delegates to the current provider in LlmProviderManager.
/// This ensures all consumers get the latest provider after a hot-swap.
/// </summary>
public class DelegatingLlmProvider : ILlmProvider
{
    private readonly LlmProviderManager _manager;

    public DelegatingLlmProvider(LlmProviderManager manager)
    {
        _manager = manager;
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var provider = _manager.CurrentProvider
            ?? throw new InvalidOperationException(
                "LLM provider is not configured. Configure it via Admin Settings.");
        return provider.CompleteAsync(request, cancellationToken);
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        var provider = _manager.CurrentProvider
            ?? throw new InvalidOperationException(
                "LLM provider is not configured. Configure it via Admin Settings.");
        return provider.CountTokensAsync(text, cancellationToken);
    }
}
