using Beacon.AI.Services.Knowledge;

namespace Beacon.MCP.Services;

/// <summary>
/// Value-grounding probe slots from <see cref="McpQueryConcurrencyLimiter"/>, under the MCP caller's credential key: a
/// probe that runs beside the call counts as one more query of that credential. A caller without a credential (the shared
/// anonymous bucket) gets none, so its probes run one at a time.
/// </summary>
internal sealed class McpProbeConcurrencyLeases(IProjectContext projectContext, McpQueryConcurrencyLimiter limiter) : IProbeConcurrencyLeases
{
    public IDisposable? TryAcquire(int limit)
    {
        var callerKey = McpQueryConcurrencyLimiter.CallerKeyOf(projectContext);

        return callerKey == McpQueryConcurrencyLimiter.AnonymousKey
            ? null
            : limiter.TryAcquire(callerKey, limit);
    }
}
