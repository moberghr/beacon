using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class McpAuditLog : BaseEntity
{
    public int? SessionId { get; set; }
    public int? UserId { get; set; }
    public required string Tool { get; set; }
    public string? Parameters { get; set; }

    public int? DataSourceId { get; set; }
    public int ExecutionTimeMs { get; set; }
    public int? ResultRowCount { get; set; }
    public string? ErrorMessage { get; set; }

    public McpSession? Session { get; set; }
    public SemanticoUser? User { get; set; }
}
