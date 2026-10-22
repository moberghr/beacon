using Amazon.Runtime;

namespace Beacon.AI.Services.LlmProviders;

/// <summary>
/// The Bedrock client's retry policy. The request queue is the one retry layer, so this policy retries no failure of
/// its own (<see cref="RetryForException"/> is always false). The base <see cref="RetryPolicy.Retry"/> still runs the
/// SDK's clock-skew check first: a request refused with a clock-skew error code while the service's <c>Date</c> is more
/// than four minutes from the local clock is re-signed with the correction the SDK recorded from that response and sent
/// once more, without a wait. Any other failure, an auth failure while the clocks agree included, is not retried here.
/// </summary>
internal sealed class ClockSkewRetryPolicy : RetryPolicy
{
    public ClockSkewRetryPolicy()
    {
        MaxRetries = 1;
    }

    public override bool CanRetry(IExecutionContext executionContext)
    {
        return executionContext.RequestContext.Request.IsRequestStreamRewindable();
    }

    public override bool RetryForException(IExecutionContext executionContext, Exception exception)
    {
        return false;
    }

    public override Task<bool> RetryForExceptionAsync(IExecutionContext executionContext, Exception exception)
    {
        return Task.FromResult(false);
    }

    public override bool RetryLimitReached(IExecutionContext executionContext)
    {
        return executionContext.RequestContext.Retries >= MaxRetries;
    }

    public override void WaitBeforeRetry(IExecutionContext executionContext)
    {
    }

    public override Task WaitBeforeRetryAsync(IExecutionContext executionContext)
    {
        return Task.CompletedTask;
    }
}
