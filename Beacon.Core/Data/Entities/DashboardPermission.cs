using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class DashboardPermission : BaseEntity
{
    public required int DashboardId { get; set; }
    public required string UserId { get; set; } // max 100 chars
    public required DashboardPermissionLevel PermissionLevel { get; set; }
    public string? GrantedByUserId { get; set; } // max 100 chars
    public required DateTime GrantedAt { get; set; }

    // Navigation properties
    public Dashboard Dashboard { get; set; } = null!;
}
