using Beacon.Core.Authorization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Models.ControlTower;

public record GetControlTowerDataRequest : ListRequest
{
    /// <summary>
    /// Where the caller may read: a subscription is shown as its query. Required, so no caller sees every subscription
    /// by leaving it out; <see cref="ProjectSet.All"/> is an explicit choice.
    /// </summary>
    public required ProjectSet Readable { get; init; }

    public int? DataSourceId { get; init; }
    public int? FolderId { get; init; }
    public HealthStatus? HealthStatus { get; init; }
    public bool? HasUnresolvedTasks { get; init; }
    public string? SearchKeyword { get; init; }

    /// <summary>
    /// Window for execution statistics, anomalies, and Stalled detection. Defaults to 30 days.
    /// </summary>
    public int TimeRangeDays { get; init; } = 30;
}
