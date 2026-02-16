using Semantico.Core.Helpers;

namespace Semantico.Core.Models.ControlTower;

public class ControlTowerHealthListData : IPagedListResponse<ControlTowerSubscriptionHealthData>
{
    public List<ControlTowerSubscriptionHealthData> Data { get; set; } = new();
    public int? TotalCount { get; set; }
}
