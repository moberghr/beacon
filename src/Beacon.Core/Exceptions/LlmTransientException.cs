namespace Beacon.Core.Exceptions;

/// <summary>
/// An LLM provider failed in a way that may pass (rate limited, overloaded, unavailable or timed out) and did not retry
/// itself: the request queue retries it, waiting <see cref="RetryAfter"/> when the provider asked for a wait.
/// </summary>
public sealed class LlmTransientException : AiServiceException
{
    public LlmTransientException(string message, TimeSpan? retryAfter = null)
        : base(message)
    {
        RetryAfter = retryAfter;
    }

    public LlmTransientException(string message, TimeSpan? retryAfter, Exception innerException)
        : base(message, innerException)
    {
        RetryAfter = retryAfter;
    }

    /// <summary>The wait the provider asked for before the next attempt; null when it asked for none.</summary>
    public TimeSpan? RetryAfter { get; }
}
