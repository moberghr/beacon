using Beacon.Core.Helpers;
using Beacon.Core.Models.Ai;
using Microsoft.Extensions.Logging;

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
    private readonly ILogger<DelegatingLlmProvider>? _logger;

    public DelegatingLlmProvider(LlmProviderManager manager, LlmRequestQueue queue, ILogger<DelegatingLlmProvider>? logger = null)
    {
        _logger = logger;
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _queue = queue ?? throw new ArgumentNullException(nameof(queue));
    }

    public async Task<LlmResponse> CompleteAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        // One snapshot per call: a fast provider is never paired with a main provider from another configuration.
        var (main, fast) = _manager.ActiveProvidersSnapshot;
        main ??= RequireProvider();

        if (request.Tier == LlmTier.Fast && fast is not null)
        {
            try
            {
                return await Enqueue(fast, request, cancellationToken);
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // The failure's types and codes only: an SDK message can carry credential or model details.
                _logger?.LogWarning(
                    "Fast-tier LLM call failed ({Failure}). Serving the request on the main provider.",
                    FailureCodes.Describe(ex));
            }
        }

        return await Enqueue(main, request, cancellationToken);
    }

    public Task<TokenCount> CountTokensAsync(string text, CancellationToken cancellationToken = default)
    {
        var provider = RequireProvider();
        return provider.CountTokensAsync(text, cancellationToken);
    }

    private Task<LlmResponse> Enqueue(ILlmProvider provider, LlmRequest request, CancellationToken cancellationToken)
    {
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

    // When the configured provider is not installed, the manager's install message says what to do; otherwise the
    // provider is not configured (no key yet) or could not be built.
    private ILlmProvider RequireProvider()
    {
        return _manager.CurrentProvider
            ?? throw new InvalidOperationException(
                _manager.InactiveReason ?? "LLM provider is not configured. Configure it via Admin Settings.");
    }
}
