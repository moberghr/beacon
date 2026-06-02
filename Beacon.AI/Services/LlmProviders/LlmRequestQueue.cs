using Beacon.Core.Exceptions;
using Beacon.Core.Models.Ai;

namespace Beacon.AI.Services.LlmProviders;

public class LlmRequestQueue
{
    private readonly SemaphoreSlim _rateLimiter;
    private readonly int _maxRetries;
    private readonly TimeSpan _baseDelay;

    public LlmRequestQueue(int maxConcurrent = 50, int maxRetries = 3)
    {
        _rateLimiter = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        _maxRetries = maxRetries;
        _baseDelay = TimeSpan.FromSeconds(1);
    }

    public async Task<LlmResponse> EnqueueRequestAsync(
        ILlmProvider provider,
        LlmRequest request,
        CancellationToken cancellationToken = default)
    {
        await _rateLimiter.WaitAsync(cancellationToken);
        try
        {
            return await ExecuteWithRetryAsync(provider, request, cancellationToken);
        }
        finally
        {
            _rateLimiter.Release();
        }
    }

    private async Task<LlmResponse> ExecuteWithRetryAsync(
        ILlmProvider provider,
        LlmRequest request,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        for (int attempt = 0; attempt < _maxRetries; attempt++)
        {
            try
            {
                return await provider.CompleteAsync(request, cancellationToken);
            }
            catch (Exception ex) when (IsRetryable(ex) && attempt < _maxRetries - 1)
            {
                lastException = ex;
                var delay = CalculateExponentialBackoff(attempt);
                await Task.Delay(delay, cancellationToken);
            }
        }

        throw new AiServiceException(
            $"Failed to complete LLM request after {_maxRetries} attempts",
            lastException!);
    }

    private static bool IsRetryable(Exception ex)
    {
        // Retry on rate limit, timeout, or transient network errors
        var message = ex.Message.ToLowerInvariant();
        return message.Contains("rate limit") ||
               message.Contains("429") ||
               message.Contains("timeout") ||
               message.Contains("503") ||
               message.Contains("504") ||
               ex is HttpRequestException ||
               ex is TaskCanceledException;
    }

    private TimeSpan CalculateExponentialBackoff(int attempt)
    {
        // Exponential backoff: 1s, 2s, 4s, 8s, etc.
        var delaySeconds = Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(delaySeconds);
    }
}
