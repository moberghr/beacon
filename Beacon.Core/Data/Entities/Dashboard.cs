using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Data.Entities;

public class Dashboard : ArchivableBaseEntity
{
    public required string Name { get; set; } // max 200 chars
    public string? Description { get; set; } // max 1000 chars
    public string? CreatedByUserId { get; set; } // max 100 chars
    public string? CreatedByUserName { get; set; } // max 200 chars
    public bool IsShared { get; set; } = false;
    public bool IsDefault { get; set; } = false; // Show to all users on first visit
    public int? RefreshIntervalSeconds { get; set; }
    public string? LayoutConfiguration { get; set; } // JSON: grid layout positions
    public int SortOrder { get; set; } = 0;

    // Navigation properties
    public List<DashboardWidget> Widgets { get; set; } = new();
    public List<DashboardPermission> Permissions { get; set; } = new();
}
