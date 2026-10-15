using System.Globalization;

namespace Beacon.MCP.Services;

/// <summary>
/// Enforces <c>MaxConcurrentQueriesPerKey</c>: how many SQL-carrying MCP tool calls (<c>query</c>, <c>ask</c>,
/// <c>dry_run</c> and the saved-query tools) one caller credential may have in flight at once. The credential is the
/// caller's API key, else a mapped MCP caller's subject hash, else its Beacon user (<see cref="CallerKeyOf"/>). A call
/// over the limit is refused at once — it never queues — and the tool answers <see cref="RefusalMessage"/> through its
/// usual (audited) error path.
/// <para>
/// The count is kept in process memory, so the limit holds <b>per Beacon instance</b>: behind a load balancer with N
/// instances one credential can run up to N times the limit. Registered as a singleton so every request on the
/// instance shares the count.
/// </para>
/// </summary>
internal sealed class McpQueryConcurrencyLimiter
{
    public const string RefusalMessage =
        "Too many concurrent queries for this credential; retry shortly.";

    // Every caller that carries no identifier at all shares one bucket, so such callers are limited together rather than
    // not at all.
    internal const string AnonymousKey = "anonymous";

    private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    /// <summary>
    /// The credential <paramref name="context"/>'s calls are counted under: the API key id, else the mapped MCP caller's
    /// subject hash, else the Beacon user id, else the shared anonymous bucket.
    /// </summary>
    public static string CallerKeyOf(IProjectContext context)
    {
        if (context.ApiKeyId is { } apiKeyId)
        {
            return "api-key:" + apiKeyId.ToString(CultureInfo.InvariantCulture);
        }

        if (!string.IsNullOrEmpty(context.CallerHash))
        {
            return "caller:" + context.CallerHash;
        }

        return context.UserId is { } userId
            ? "user:" + userId.ToString(CultureInfo.InvariantCulture)
            : AnonymousKey;
    }

    /// <summary>
    /// Takes one of <paramref name="limit"/> slots for <paramref name="callerKey"/>. Returns the lease that frees the
    /// slot when disposed (dispose it when the call completes, succeeds or fails), or <c>null</c> when the caller already
    /// has <paramref name="limit"/> calls in flight. A limit below 1 is treated as 1.
    /// </summary>
    public IDisposable? TryAcquire(string callerKey, int limit)
    {
        var effectiveLimit = Math.Max(1, limit);
        lock (_sync)
        {
            _inFlight.TryGetValue(callerKey, out var current);
            if (current >= effectiveLimit)
            {
                return null;
            }

            _inFlight[callerKey] = current + 1;
        }

        return new Lease(this, callerKey);
    }

    /// <summary>The calls <paramref name="callerKey"/> has in flight on this instance.</summary>
    internal int InFlight(string callerKey)
    {
        lock (_sync)
        {
            return _inFlight.TryGetValue(callerKey, out var current) ? current : 0;
        }
    }

    private void Release(string callerKey)
    {
        lock (_sync)
        {
            if (!_inFlight.TryGetValue(callerKey, out var current))
            {
                return;
            }

            // Idle callers leave the map, so it holds only credentials with calls in flight.
            if (current <= 1)
            {
                _inFlight.Remove(callerKey);
                return;
            }

            _inFlight[callerKey] = current - 1;
        }
    }

    private sealed class Lease(McpQueryConcurrencyLimiter owner, string callerKey) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                owner.Release(callerKey);
            }
        }
    }
}
