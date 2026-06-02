namespace Beacon.Core.Models.Dashboards;

public record DashboardListData
{
    public int Id { get; init; }
    public string Name { get; init; } = null!;
    public string? Description { get; init; }
    public bool IsShared { get; init; }
    public bool IsDefault { get; init; }
    public int WidgetCount { get; init; }
    public DateTime CreatedTime { get; init; }
    public bool IsOwner { get; init; }
    public string? CreatedByUserName { get; init; }
}
