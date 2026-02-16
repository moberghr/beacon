using Semantico.Core.Data.Enums;

namespace Semantico.Core.Models.Dashboards;

public record DashboardDetailsData
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public string? CreatedByUserId { get; init; }
    public string? CreatedByUserName { get; init; }
    public bool IsShared { get; init; }
    public bool IsDefault { get; init; }
    public int? RefreshIntervalSeconds { get; init; }
    public string? LayoutConfiguration { get; init; }
    public DateTime CreatedTime { get; init; }
    public List<DashboardWidgetData> Widgets { get; init; } = new();
    public DashboardPermissionLevel? UserPermissionLevel { get; init; }
}

public record DashboardWidgetData
{
    public int Id { get; init; }
    public string Title { get; init; } = null!;
    public WidgetType WidgetType { get; init; }
    public string ConfigurationJson { get; init; } = null!;
    public int PositionX { get; init; }
    public int PositionY { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int SortOrder { get; init; }
    public int? RefreshIntervalSeconds { get; init; }
}
