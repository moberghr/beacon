using Beacon.Core.Helpers;

namespace Beacon.Core.Models.Dashboards;

/// <summary>Default dashboard first, then its sort order, then newest, unless <c>sort</c> says otherwise.</summary>
public record GetDashboardsRequest : ListRequest
{
    public bool? IsShared { get; init; }

    public bool? IsDefault { get; init; }

    public string? SearchKeyword { get; init; }
}
