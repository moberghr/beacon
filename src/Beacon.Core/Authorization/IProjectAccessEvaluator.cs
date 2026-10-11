namespace Beacon.Core.Authorization;

/// <summary>
/// Decides which projects, and through them which data sources, a caller may act in, per <see cref="ProjectAction"/>.
/// It unifies Admin access, project membership, open projects and the project lists API keys and MCP callers carry.
/// Scoped and memoized per scope: one database command per caller and scope, never cached across requests. Registered
/// with <c>TryAdd</c>, so a host can replace it.
/// </summary>
public interface IProjectAccessEvaluator
{
    /// <summary>
    /// The current request's caller. Without a request: the owner the scope runs as (<see cref="IRunAsScope"/>), else the
    /// system (unrestricted) in a scope marked as system work (<see cref="ISystemWorkScope"/>), nothing in any other scope.
    /// </summary>
    Task<ProjectAccessScope> GetScopeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// An explicit actor: the owner of scheduled work (<see cref="IBeaconActorAccessor.ResolveOwnerAsync"/>), or a
    /// caller's actor read under other rules (an API key's owner without the Admin bypass, when a key is issued).
    /// </summary>
    Task<ProjectAccessScope> GetScopeAsync(BeaconActor actor, CancellationToken cancellationToken);
}
