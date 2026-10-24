using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class McpAuditLog : BaseEntity
{
    public int? UserId { get; set; }
    public required string Tool { get; set; }
    public string? Parameters { get; set; }

    public int? DataSourceId { get; set; }
    public int? ProjectId { get; set; }
    public int ExecutionTimeMs { get; set; }
    public int? ResultRowCount { get; set; }
    public string? ErrorMessage { get; set; }

    /// <summary><c>User</c> or <c>System</c> for a mapped JWT caller; null for API keys and cookie sessions.</summary>
    public string? CallerKind { get; set; }

    /// <summary>HMAC-SHA256 hex of the JWT caller's tenant and subject (never the raw oid or email).</summary>
    public string? CallerHash { get; set; }

    /// <summary>
    /// Minted by Beacon for every audited call (32 hex characters) and never taken from a request: the one identifier on
    /// the row, and on the audit log event, that a caller cannot choose. Null on rows written before it existed.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>W3C trace id (32 hex chars) of the activity current when the call was audited.</summary>
    public string? TraceId { get; set; }

    /// <summary>W3C span id (16 hex chars) of the activity current when the call was audited.</summary>
    public string? SpanId { get; set; }

    /// <summary>The <c>Mcp-Session-Id</c> transport header; null for stateless clients.</summary>
    public string? McpSessionId { get; set; }

    /// <summary>
    /// The upstream request id header named by <c>Beacon:Mcp:Audit:RequestIdHeader</c>, as the caller sent it (length-capped
    /// and limited to a safe character set). Supplied by the caller and not verified: <see cref="CorrelationId"/> is the
    /// identifier Beacon vouches for.
    /// </summary>
    public string? UpstreamRequestId { get; set; }

    /// <summary>The <c>api_key_id</c> claim of an API-key caller. No FK — the audit must outlive a deleted key.</summary>
    public int? ApiKeyId { get; set; }

    /// <summary>
    /// Keyed hash of the call's parameters as received, written whether or not the parameters themselves are retained, so
    /// two calls with the same input can be matched without keeping it.
    /// </summary>
    public string? ContentHash { get; set; }

    public BeaconUser? User { get; set; }

    /// <summary>The statements the call ran on data sources; deleted with the row.</summary>
    public List<McpAuditStatement> Statements { get; set; } = new();
}
