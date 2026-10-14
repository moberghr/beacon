using Beacon.Core.Data.Enums;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerStatisticsHandler(IControlTowerService controlTowerService, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetControlTowerStatisticsQuery, GetControlTowerStatisticsResult>
{
    public async Task<GetControlTowerStatisticsResult> Handle(
        GetControlTowerStatisticsQuery request,
        CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var serviceRequest = new GetControlTowerDataRequest
        {
            Readable = scope.For(ProjectAction.Read),
            DataSourceId = request.DataSourceId,
            FolderId = request.FolderId,
            HealthStatus = request.HealthStatus,
            HasUnresolvedTasks = request.HasUnresolvedTasks,
            SearchKeyword = request.SearchKeyword,
            TimeRangeDays = request.TimeRangeDays
        };

        var stats = await controlTowerService.GetControlTowerStatistics(serviceRequest, cancellationToken);

        return new GetControlTowerStatisticsResult(stats);
    }
}

public record GetControlTowerStatisticsQuery(
    [property: ProjectScopeExempt("filter only: the list is scope-filtered")] int? DataSourceId = null,
    [property: ProjectScopeExempt("filter only: the list is scope-filtered")] int? FolderId = null,
    HealthStatus? HealthStatus = null,
    bool? HasUnresolvedTasks = null,
    string? SearchKeyword = null,
    int TimeRangeDays = 30) : IRequest<GetControlTowerStatisticsResult>, IProjectFilteredRequest;

public record GetControlTowerStatisticsResult(ControlTowerStatistics Statistics);
