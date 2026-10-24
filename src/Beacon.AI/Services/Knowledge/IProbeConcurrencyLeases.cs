namespace Beacon.AI.Services.Knowledge;

/// <summary>
/// The per-credential query slots (<c>MaxConcurrentQueriesPerKey</c>) value-grounding probes may take to run beside the
/// call they belong to. The call's own probe runs in the call's slot; each further probe that runs at the same time holds
/// a slot taken here, so the statements one credential has in flight never exceed its limit. Beacon.MCP serves the MCP
/// caller's slots; without it (or for a caller it cannot key) no slot is given and the probes run one at a time.
/// </summary>
public interface IProbeConcurrencyLeases
{
    /// <summary>
    /// One more slot of the current caller's <paramref name="limit"/>, freed when the lease is disposed; null when none is
    /// free now (it never waits).
    /// </summary>
    IDisposable? TryAcquire(int limit);
}

/// <summary>No slots beyond the call's own: the probes of a call run one at a time.</summary>
internal sealed class NoProbeConcurrencyLeases : IProbeConcurrencyLeases
{
    public IDisposable? TryAcquire(int limit) => null;
}
