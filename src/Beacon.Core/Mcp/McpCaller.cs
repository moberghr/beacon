using System.Security.Claims;

namespace Beacon.Core.Mcp;

public enum McpCallerKind
{
    User,
    System
}

/// <summary>
/// The scope a JWT caller gets on MCP. Emitted as the same <c>scope</c> claim values API keys use, so the
/// Execute-scope policy treats both identically: <see cref="Read"/> cannot reach <c>/beacon/mcp</c>.
/// </summary>
public enum McpCallerScope
{
    Read,
    Execute
}

/// <summary>
/// The Beacon-side identity of a validated JWT caller on MCP, as decided by an <see cref="IMcpCallerMapper"/>.
/// </summary>
/// <param name="Kind">User (delegated token) or System (app-only token or service user).</param>
/// <param name="Name">The system name, or the user's display name. Never logged.</param>
/// <param name="SubjectHash">Lowercase hex HMAC-SHA256 of the tenant and subject (see <see cref="McpCallerSubjectHasher"/>). Safe for audit rows and logs (§1.11).</param>
/// <param name="BeaconUserId">The provisioned <c>BeaconUser.Id</c>, when there is one.</param>
/// <param name="AllowedProjectIds">The only projects the caller may touch. Empty denies all.</param>
/// <param name="Scope">Read or Execute.</param>
/// <param name="HostClaims">The permission set the caller gets inside the host application.</param>
public sealed record McpCaller(
    McpCallerKind Kind,
    string Name,
    string SubjectHash,
    int? BeaconUserId,
    IReadOnlyList<int> AllowedProjectIds,
    McpCallerScope Scope,
    IReadOnlyList<Claim> HostClaims);

/// <summary>
/// Claim types and values minted for mapped MCP callers. Token-supplied claims of these types are stripped
/// by the JWT middleware so a token can never grant itself projects, a scope, or an audit identity.
/// </summary>
public static class McpCallerClaimTypes
{
    public const string AuthMethod = "auth_method";
    public const string AllowedProjects = "allowed_projects";
    public const string Scope = "scope";
    public const string CallerKind = "caller_kind";
    public const string CallerHash = "caller_hash";
    public const string ApiKeyId = "api_key_id";
    public const string ApiKeyName = "api_key_name";

    /// <summary><c>auth_method</c> value for a JWT caller on the MCP route. Scope-gated like an API key.</summary>
    public const string McpCallerAuthMethod = "mcp_caller";

    /// <summary><c>auth_method</c> value for a JWT caller on any other route. Not scope-gated.</summary>
    public const string JwtAuthMethod = "jwt";

    /// <summary>Claim types a bearer token must never be able to set for itself.</summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AuthMethod,
        AllowedProjects,
        Scope,
        CallerKind,
        CallerHash,
        ApiKeyId,
        ApiKeyName
    };
}
