using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.ControlTower;

public class GetControlTowerDataRequest : BaseListRequest
{
    public int? DataSourceId { get; init; }
    public int? FolderId { get; init; }
    public HealthStatus? HealthStatus { get; init; }
    public bool? HasUnresolvedTasks { get; init; }
    public string? SearchKeyword { get; init; }
}
