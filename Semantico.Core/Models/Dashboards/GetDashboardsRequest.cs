using Semantico.Core.Helpers;

namespace Semantico.Core.Models.Dashboards;

public class GetDashboardsRequest : BaseListRequest
{
    public bool? IsShared { get; init; }
    public bool? IsDefault { get; init; }
    public string? SearchKeyword { get; init; }
}
