using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class McpAuditLog : BaseEntity
{
    public int? SessionId { get; set; }
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

    public McpSession? Session { get; set; }
    public BeaconUser? User { get; set; }
}
