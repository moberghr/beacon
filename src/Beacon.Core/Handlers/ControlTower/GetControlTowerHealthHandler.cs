using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerHealthHandler(ControlTowerService controlTowerService, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetControlTowerHealthQuery, PagedList<ControlTowerSubscriptionHealthData>>
{
    public async Task<PagedList<ControlTowerSubscriptionHealthData>> Handle(
        GetControlTowerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var serviceRequest = new GetControlTowerDataRequest
        {
            Readable = scope.For(ProjectAction.Read),
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

        return await controlTowerService.GetSubscriptionHealthOverview(serviceRequest, cancellationToken);
    }
}

/// <summary>
/// Sortable by <c>queryName</c>, <c>successRate</c>, <c>totalExecutions</c> and <c>unresolvedTaskCount</c>;
/// without a sort the list is worst first (most open tasks, then lowest success rate).
/// </summary>
public record GetControlTowerHealthQuery : ListRequest, IRequest<PagedList<ControlTowerSubscriptionHealthData>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? DataSourceId { get; init; }

    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? FolderId { get; init; }

    public HealthStatus? HealthStatus { get; init; }

    public bool? HasUnresolvedTasks { get; init; }

    public string? SearchKeyword { get; init; }

    /// <summary>Window for execution statistics, anomalies and stalled detection; defaults to 30 days.</summary>
    public int? TimeRangeDays { get; init; }
}
