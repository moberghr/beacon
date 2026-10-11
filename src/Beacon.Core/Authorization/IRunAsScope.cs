namespace Beacon.Core.Authorization;

/// <summary>
/// Runs the current DI scope as the owner of scheduled work: a subscription's scheduled run, a data contract's scheduled
/// evaluation, an AI actor's background think cycle. The job's entry point sets it once, before anything in the scope
/// resolves its caller; from then on <see cref="IBeaconActorAccessor.GetCurrentAsync"/> is that owner, resolved as they
/// are now (<see cref="IBeaconActorAccessor.ResolveOwnerAsync"/>), and <see cref="IProjectAccessEvaluator"/> decides
/// for the owner. A scope that runs as an owner takes precedence over the system-work marker
/// (<see cref="ISystemWorkScope"/>) of the job host. Never in a request: there the request's caller is the actor.
/// </summary>
public interface IRunAsScope
{
    /// <summary>True once <see cref="RunAs"/> was called in this scope.</summary>
    bool IsSet { get; }

    /// <summary>The owner's <c>Users.ExternalId</c> the scope runs as; null for an ownerless row, or before <see cref="RunAs"/>.</summary>
    string? OwnerUserId { get; }

    /// <summary>
    /// Runs the scope as <paramref name="ownerUserId"/>, the owner the work recorded (null: an ownerless row, which
    /// reaches only the openly reachable data sources). Throws <see cref="InvalidOperationException"/> when called a
    /// second time, inside a request (an HTTP context), or after the scope's caller was already resolved.
    /// </summary>
    void RunAs(string? ownerUserId);
}
