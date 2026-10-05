using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerHealthHandler(IControlTowerService controlTowerService)
    : IRequestHandler<GetControlTowerHealthQuery, PagedList<ControlTowerSubscriptionHealthData>>
{
    public Task<PagedList<ControlTowerSubscriptionHealthData>> Handle(
        GetControlTowerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var serviceRequest = new GetControlTowerDataRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            DataSourceId = request.DataSourceId,
            FolderId = request.FolderId,
            HealthStatus = request.HealthStatus,
            HasUnresolvedTasks = request.HasUnresolvedTasks,
            SearchKeyword = request.SearchKeyword,
            TimeRangeDays = request.TimeRangeDays ?? 30
        };

        return controlTowerService.GetSubscriptionHealthOverview(serviceRequest, cancellationToken);
    }
}

/// <summary>
/// Sortable by <c>queryName</c>, <c>successRate</c>, <c>totalExecutions</c> and <c>unresolvedTaskCount</c>;
/// without a sort the list is worst first (most open tasks, then lowest success rate).
/// </summary>
public record GetControlTowerHealthQuery : ListRequest, IRequest<PagedList<ControlTowerSubscriptionHealthData>>
{
    public int? DataSourceId { get; init; }

    public int? FolderId { get; init; }

    public HealthStatus? HealthStatus { get; init; }

    public bool? HasUnresolvedTasks { get; init; }

    public string? SearchKeyword { get; init; }

    /// <summary>Window for execution statistics, anomalies and stalled detection; defaults to 30 days.</summary>
    public int? TimeRangeDays { get; init; }
}
