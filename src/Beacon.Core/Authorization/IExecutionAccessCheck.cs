namespace Beacon.Core.Authorization;

/// <summary>
/// Execute-time re-validation: right before work runs SQL or an LLM call on data sources, whether the scope's caller may
/// still take an action on every one of them, through one project that links them all or through the openly reachable
/// sources. The caller is the request's caller, or, in a scope that runs as the owner of scheduled work
/// (<see cref="IRunAsScope"/>), that owner as they are now. A refusal is logged with ids only: as scheduled work skipped
/// when the scope runs as an owner, as a refused access otherwise. Registered with <c>TryAdd</c>.
/// </summary>
public interface IExecutionAccessCheck
{
    /// <param name="action">What the work does: <see cref="ProjectAction.Run"/> for stored SQL, <see cref="ProjectAction.Query"/> for ad-hoc SQL and LLM work.</param>
    /// <param name="dataSourceIds">The data sources the work is about to read; none is never allowed except where the caller reaches everything.</param>
    /// <param name="resource">The kind of work, for the log (for example <c>subscription</c>).</param>
    /// <param name="resourceId">The work's id, for the log.</param>
    /// <param name="cancellationToken">Cancels the check.</param>
    Task<bool> AllowsAsync(
        ProjectAction action,
        IReadOnlyCollection<int> dataSourceIds,
        string resource,
        int resourceId,
        CancellationToken cancellationToken);
}
