using Semantico.Core.Data.Entities.Base;

namespace Semantico.Core.Data.Entities;

public class McpSession : BaseEntity
{
    public int? ApiKeyId { get; set; }
    public int? UserId { get; set; }
    public required string SessionId { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastActivityAt { get; set; } = DateTime.UtcNow;

    public int QueriesExecuted { get; set; }
    public int TokensUsed { get; set; }

    public ApiKeyCredential? ApiKey { get; set; }
    public SemanticoUser? User { get; set; }
}
