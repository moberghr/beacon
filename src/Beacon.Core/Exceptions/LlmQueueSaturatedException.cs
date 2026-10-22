namespace Beacon.Core.Exceptions;

/// <summary>
/// The LLM request queue could not give the request a slot: too many requests were already waiting, or none came free
/// within the wait limit. Nothing was sent to the provider; the caller may try again later.
/// </summary>
public sealed class LlmQueueSaturatedException : AiServiceException
{
    private const string BusyMessage = "The LLM is busy. Try again later.";

    public LlmQueueSaturatedException()
        : base(BusyMessage)
    {
    }

    /// <summary>The queue found no slot for a retry; <paramref name="innerException"/> is the provider failure being retried.</summary>
    public LlmQueueSaturatedException(Exception innerException)
        : base(BusyMessage, innerException)
    {
    }
}
