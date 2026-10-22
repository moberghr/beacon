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
        ArgumentNullException.ThrowIfNull(request);

        // The provider that serves the call and the configuration its span names come from one snapshot, so a concurrent
        // hot-swap never tags a call with another provider's kind or model.
        var (provider, configuration) = _manager.CurrentSnapshot();
        if (provider == null)
        {
            throw NotConfigured();
        }

        // Recursion guard: pass the INNER provider to the queue, never `this`. If `this` were
        // passed, the queue would call back into DelegatingLlmProvider.CompleteAsync, which
        // would re-enqueue forever.
        if (ReferenceEquals(provider, this))
        {
            throw new InvalidOperationException(
                "LlmProviderManager.CurrentProvider must not be the DelegatingLlmProvider itself.");
        }

        // One span per completion, around the queue so it covers the wait for a slot and the retries too.
        return LlmCompletionTelemetry.TraceAsync(
            configuration.Provider.ToString().ToLowerInvariant(),
            configuration.Model,
            request,
            () => _queue.EnqueueRequestAsync(provider, request, cancellationToken));
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        var provider = RequireProvider();
        return provider.CountTokensAsync(text, cancellationToken);
    }

    // When the configured provider is not installed, the manager's install message says what to do; otherwise the
    // provider is not configured (no key yet) or could not be built.
    private ILlmProvider RequireProvider()
    {
        return _manager.CurrentProvider ?? throw NotConfigured();
    }

    private InvalidOperationException NotConfigured()
    {
        return new InvalidOperationException(
            _manager.InactiveReason ?? "LLM provider is not configured. Configure it via Admin Settings.");
    }
}
