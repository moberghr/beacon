using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// A proxy <see cref="ILlmProvider"/> that delegates to the current provider in
/// <see cref="LlmProviderManager"/>, ensuring all consumers see the latest provider after a
/// hot-swap. All completion calls are funnelled through <see cref="LlmRequestQueue"/> so the
/// configured <c>MaxConcurrentRequests</c> limit and retry policy apply uniformly — callers
/// must NOT inject concrete providers and never need to depend on the queue directly.
/// </summary>
public class DelegatingLlmProvider : ILlmProvider
{
    private readonly LlmProviderManager _manager;
    private readonly LlmRequestQueue _queue;

    public DelegatingLlmProvider(LlmProviderManager manager, LlmRequestQueue queue)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        var provider = _manager.CurrentProvider
            ?? throw new InvalidOperationException(
                "LLM provider is not configured. Configure it via Admin Settings.");

        // Recursion guard: pass the INNER provider to the queue, never `this`. If `this` were
        // passed, the queue would call back into DelegatingLlmProvider.CompleteAsync, which
        // would re-enqueue forever.
        if (ReferenceEquals(provider, this))
        {
            throw new InvalidOperationException(
                "LlmProviderManager.CurrentProvider must not be the DelegatingLlmProvider itself.");
        }

        return _queue.EnqueueRequestAsync(provider, request, cancellationToken);
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        var provider = _manager.CurrentProvider
            ?? throw new InvalidOperationException(
                "LLM provider is not configured. Configure it via Admin Settings.");
        return provider.CountTokensAsync(text, cancellationToken);
    }
}
