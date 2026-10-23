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

    /// <summary>W3C trace id (32 hex chars) of the activity current when the call was audited.</summary>
    public string? TraceId { get; set; }

    /// <summary>W3C span id (16 hex chars) of the activity current when the call was audited.</summary>
    public string? SpanId { get; set; }

    /// <summary>The <c>Mcp-Session-Id</c> transport header; null for stateless clients.</summary>
    public string? McpSessionId { get; set; }

    /// <summary>The sanitised upstream request id header named by <c>Beacon:Mcp:Audit:RequestIdHeader</c>.</summary>
    public string? UpstreamRequestId { get; set; }

    /// <summary>The <c>api_key_id</c> claim of an API-key caller. No FK — the audit must outlive a deleted key.</summary>
    public int? ApiKeyId { get; set; }

    /// <summary>
    /// Keyed hash of the call's parameters as received, written whether or not the parameters themselves are retained, so
    /// two calls with the same input can be matched without keeping it.
    /// </summary>
    public string? ContentHash { get; set; }

    /// <summary>
    /// The row's own id within the seal, unique across the table, so a sealed row cannot be copied into a second one. Null
    /// on rows written before rows were sealed.
    /// </summary>
    public Guid? AuditGuid { get; set; }

    /// <summary>
    /// Which field list the seal covers (<c>McpAuditSeal.CurrentVersion</c>), so a field added later is sealed without
    /// breaking the rows sealed before it. Null on rows written before rows were sealed.
    /// </summary>
    public int? SealVersion { get; set; }

    /// <summary>The id of the integrity key the row was sealed under (the security audit trail's key ids).</summary>
    public string? HmacKeyId { get; set; }

    /// <summary>
    /// The keyed HMAC-SHA256 over the row's fields and its statements, written when the row is saved
    /// (<c>McpAuditSeal</c>); null on rows written before rows were sealed.
    /// </summary>
    public string? Hmac { get; set; }

    public BeaconUser? User { get; set; }

    /// <summary>The statements the call ran on data sources; deleted with the row.</summary>
    public List<McpAuditStatement> Statements { get; set; } = new();
}
