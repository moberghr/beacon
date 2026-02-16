using Semantico.Core.Data.Enums;
using Semantico.Core.Helpers;

namespace Semantico.Core.Models.ControlTower;

public class GetControlTowerDataRequest : BaseListRequest
{
    public int? DataSourceId { get; init; }
    public int? FolderId { get; init; }
    public HealthStatus? HealthStatus { get; init; }
    public bool? HasUnresolvedTasks { get; init; }
    public string? SearchKeyword { get; init; }
}
