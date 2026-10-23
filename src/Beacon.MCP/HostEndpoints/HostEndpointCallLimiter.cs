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

    private readonly McpQueryConcurrencyLimiter _perCaller = new();
    private readonly McpQueryConcurrencyLimiter _total = new();

    /// <summary>
    /// The slot for one call of <paramref name="callerKey"/>, freed when disposed (once), or null when the caller or all
    /// callers together are at their limit.
    /// </summary>
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
