using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Data.Entities;

public class DashboardWidget : BaseEntity
{
    public required int DashboardId { get; set; }
    public required string Title { get; set; } // max 200 chars
    public required WidgetType WidgetType { get; set; }
    public required string ConfigurationJson { get; set; } // Widget-specific config
    public int PositionX { get; set; } = 0; // Grid column position
    public int PositionY { get; set; } = 0; // Grid row position
    public int Width { get; set; } = 6; // Grid columns span (1-12)
    public int Height { get; set; } = 2; // Grid rows span (1-6)
    public int SortOrder { get; set; } = 0;
    public int? RefreshIntervalSeconds { get; set; } // Override dashboard refresh

    // Navigation properties
    public Dashboard Dashboard { get; set; } = null!;
}
