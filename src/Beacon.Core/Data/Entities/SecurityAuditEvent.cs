using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

/// <summary>
/// One security-relevant action, append-only: who acted (<see cref="ActorKind"/>, the acting <c>Users.Id</c>, the API key
/// or, for a caller without a Beacon user, a keyed hash), what was done (<see cref="Category"/>, <see cref="Action"/>,
/// <see cref="Outcome"/>), to what (<see cref="ResourceType"/>, <see cref="ResourceId"/>, <see cref="ProjectId"/>), with
/// identifiers, codes and counts in <see cref="DetailsJson"/> — never names, e-mail addresses, external ids, SQL,
/// questions, comments or secrets. Each row carries a keyed HMAC of its fields (<see cref="Hmac"/>, under the key named by
/// <see cref="KeyId"/>), so an edited or forged row is found by the Auditor's verification; a deleted row is not. No
/// foreign keys: an event outlives the user, key or project it names. Rows are added only by
/// <c>ISecurityAuditRecorder</c>; the interceptor refuses to update or delete them.
/// </summary>
public class SecurityAuditEvent : BaseEntity
{
    /// <summary>Unique per event; the id a copy shipped elsewhere (the <c>Beacon.Audit</c> log stream) is matched by.</summary>
    public Guid EventGuid { get; set; }

    /// <summary>The event family (<c>auth</c>, <c>identity</c>, <c>keys</c>, <c>access</c>, …).</summary>
    public required string Category { get; set; }

    /// <summary><c>{category}.{what happened}</c>, for example <c>identity.user_created</c>.</summary>
    public required string Action { get; set; }

    /// <summary><c>success</c>, <c>failure</c> or <c>refused</c>.</summary>
    public required string Outcome { get; set; }

    /// <summary>How the actor was authenticated: <c>user</c>, <c>session</c>, <c>api_key</c>, <c>mcp_caller</c>, <c>system</c> or <c>anonymous</c>.</summary>
    public required string ActorKind { get; set; }

    /// <summary>The acting <c>Users.Id</c> (an API key's owner); null for a caller without a stored Beacon user.</summary>
    public int? ActorUserId { get; set; }

    /// <summary>The API key the actor used; null for any other caller.</summary>
    public int? ApiKeyId { get; set; }

    /// <summary>A keyed hash naming a caller without a stored Beacon user (an SSO subject, a host session, an MCP caller).</summary>
    public string? CallerHash { get; set; }

    /// <summary>The project the action concerns, when it concerns one.</summary>
    public int? ProjectId { get; set; }

    /// <summary>The kind of resource acted on (<c>user</c>, <c>api_key</c>, <c>project</c>, …).</summary>
    public string? ResourceType { get; set; }

    /// <summary>The id of the resource acted on.</summary>
    public string? ResourceId { get; set; }

    /// <summary>Identifiers, codes, counts and allow-listed before/after values, as JSON (bounded).</summary>
    public string? DetailsJson { get; set; }

    /// <summary>W3C trace id of the activity current when the event was recorded.</summary>
    public string? TraceId { get; set; }

    /// <summary>Names the integrity key <see cref="Hmac"/> was computed with.</summary>
    public required string KeyId { get; set; }

    /// <summary>Lowercase hex HMAC-SHA256 of the event's fields.</summary>
    public required string Hmac { get; set; }
}
