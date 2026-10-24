using Beacon.Core.Data;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// One store the data retention job (<see cref="IDataRetentionService"/>) purges by age. The job resolves the target's
/// window (<c>Beacon:Retention:Targets:{Name}</c>, else <c>Beacon:Retention:Days</c>), skips it at <c>0</c> (keep forever)
/// and otherwise calls <see cref="DeleteExpiredAsync"/> with the cutoff, on a context of its own, so one failing target
/// never stops the others. Registered with <c>TryAddEnumerable</c>: a host or another Beacon component adds a store by
/// registering one more target, and its name becomes a valid <c>Targets</c> key.
/// </summary>
public interface IRetentionTarget
{
    /// <summary>The stable name the target is configured and logged by (lower-case, dash-separated).</summary>
    string Name { get; }

    /// <summary>
    /// Deletes the rows created before <paramref name="cutoff"/> (UTC) in bounded batches, never the rows another store
    /// still references, and returns how many it deleted. It saves nothing else.
    /// </summary>
    Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken);
}
