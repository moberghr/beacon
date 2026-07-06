using Beacon.Core.Helpers;

namespace Beacon.Core.Models.ControlTower;

public class ControlTowerHealthListData : IPagedListResponse<ControlTowerSubscriptionHealthData>
{
    public List<ControlTowerSubscriptionHealthData> Data { get; set; } = new();
    public int? TotalCount { get; set; }
}
