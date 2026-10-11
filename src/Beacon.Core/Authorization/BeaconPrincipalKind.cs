namespace Beacon.Core.Authorization;

/// <summary>How <see cref="BeaconActorAccessor"/> classifies a caller for the project access evaluator.</summary>
internal enum BeaconPrincipalKind
{
    /// <summary>
    /// No access: an unauthenticated request, a caller whose stored user is disabled or archived, or an API key whose
    /// owner is not found.
    /// </summary>
    Anonymous,

    /// <summary>
    /// No request at all, in a scope marked as system work (a background job, a startup task): unrestricted. Jobs that act
    /// for a user set their owner explicitly. Work without a request in an unmarked scope is <see cref="Anonymous"/>.
    /// </summary>
    System,

    /// <summary>A signed-in session (cookie, OIDC, bound REST bearer) of a stored, active Beacon user.</summary>
    User,

    /// <summary>A signed-in session with no stored Beacon user (a host principal, an unbound bearer).</summary>
    Session,

    /// <summary>An API key (<c>auth_method=api_key</c>).</summary>
    ApiKey,

    /// <summary>A mapped JWT caller on MCP (<c>auth_method=mcp_caller</c>).</summary>
    McpCaller,

    /// <summary>The owner of scheduled work, resolved by <see cref="IBeaconActorAccessor.ResolveOwnerAsync"/>.</summary>
    Owner
}
