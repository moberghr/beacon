using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class ApiKeyCredential : BaseEntity
{
    public int? UserId { get; set; }
    public required string Name { get; set; }
    public required string KeyHash { get; set; }
    public required string KeyPrefix { get; set; }

    public string? Scopes { get; set; }
    public string? AllowedProjectIds { get; set; }

    public DateTime? ExpiresAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
    public bool IsRevoked { get; set; }
    public DateTime? RevokedAt { get; set; }

    public BeaconUser? User { get; set; }
}
