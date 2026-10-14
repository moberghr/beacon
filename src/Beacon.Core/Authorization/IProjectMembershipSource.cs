namespace Beacon.Core.Authorization;

/// <summary>
/// Where project memberships come from. The default reads <c>ProjectMember</c> rows and is available while Beacon user
/// management is on. A host can supply its own (for example backed by IdP groups) with <c>TryAdd</c>; Beacon's
/// membership routes then answer 409, as the host manages membership.
/// </summary>
public interface IProjectMembershipSource
{
    /// <summary>
    /// Whether memberships exist at all; read at startup. When false, a restricted project is reachable by Admins only,
    /// and restricting a project is refused.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// The caller's role per project id, for the projects it is a member of. Null, or an exception, denies every
    /// restricted project for the request; it never opens one.
    /// </summary>
    Task<IReadOnlyDictionary<int, ProjectRole>?> GetMembershipsAsync(BeaconActor actor, CancellationToken cancellationToken);
}
