using Beacon.Core.Data.Enums;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerStatisticsHandler(IControlTowerService controlTowerService)
    : IRequestHandler<GetControlTowerStatisticsQuery, GetControlTowerStatisticsResult>
{
    public async Task<GetControlTowerStatisticsResult> Handle(
        GetControlTowerStatisticsQuery request,
        CancellationToken cancellationToken)
    {
        var serviceRequest = new GetControlTowerDataRequest
        {
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
    int? DataSourceId = null,
    int? FolderId = null,
    HealthStatus? HealthStatus = null,
    bool? HasUnresolvedTasks = null,
    string? SearchKeyword = null,
    int TimeRangeDays = 30) : IRequest<GetControlTowerStatisticsResult>;

public record GetControlTowerStatisticsResult(ControlTowerStatistics Statistics);
