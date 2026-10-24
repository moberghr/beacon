using System.Diagnostics;
using Beacon.MCP.Services;

namespace Beacon.MCP.HostEndpoints;

/// <summary>
/// Bounds the endpoint tool calls in flight: <see cref="HostEndpointToolOptions.MaxConcurrentCallsPerCaller"/> per MCP
/// caller and <see cref="HostEndpointToolOptions.MaxConcurrentCalls"/> in total. The dispatcher frees a call's slot only
/// when its endpoint has ended, so an endpoint that outlives its timeout still counts.
/// </summary>
internal sealed class HostEndpointCallLimiter(HostEndpointToolOptions options)
{
    public const string RefusalMessage = "Too many host endpoint calls are in progress; retry shortly.";

    private const string AllCallersKey = "all";

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly McpQueryConcurrencyLimiter _perCaller = new();
    private readonly McpQueryConcurrencyLimiter _total = new();

    /// <summary>
    /// The slot for one call of <paramref name="callerKey"/>, freed when disposed (once). Waits up to
    /// <see cref="HostEndpointToolOptions.QueueWait"/> for a slot, and returns null when the caller or all callers
    /// together are still at their limit after that.
    /// </summary>
    public async Task<IDisposable?> AcquireAsync(string callerKey, CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(options.QueueWait.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            var slot = TryAcquire(callerKey);
            if (slot != null)
            {
                return slot;
            }

            var remaining = TimeSpan.FromSeconds((deadline - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency);
            if (remaining <= TimeSpan.Zero)
            {
                return null;
            }

            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }

    public IDisposable? TryAcquire(string callerKey)
    {
        var caller = _perCaller.TryAcquire(callerKey, options.MaxConcurrentCallsPerCaller);
        if (caller == null)
        {
            return null;
        }

        var total = _total.TryAcquire(AllCallersKey, options.MaxConcurrentCalls);
        if (total == null)
        {
            caller.Dispose();

            return null;
        }

        return new Slot(caller, total);
    }

    private sealed class Slot(IDisposable caller, IDisposable total) : IDisposable
    {
        public void Dispose()
        {
            // Both leases free themselves once, so a repeated Dispose frees nothing more.
            total.Dispose();
            caller.Dispose();
        }
    }
}
