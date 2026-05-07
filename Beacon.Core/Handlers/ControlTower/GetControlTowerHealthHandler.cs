using Beacon.Core.Data.Enums;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerHealthHandler(IControlTowerService controlTowerService)
    : IRequestHandler<GetControlTowerHealthQuery, GetControlTowerHealthResult>
{
    public async Task<GetControlTowerHealthResult> Handle(
        GetControlTowerHealthQuery request,
        CancellationToken cancellationToken)
    {
        var serviceRequest = new GetControlTowerDataRequest
        {
            Page = request.Page,
            PageSize = request.PageSize,
            DataSourceId = request.DataSourceId,
            FolderId = request.FolderId,
            HealthStatus = request.HealthStatus,
            HasUnresolvedTasks = request.HasUnresolvedTasks,
            SearchKeyword = request.SearchKeyword,
        };

        var data = await controlTowerService.GetSubscriptionHealthOverview(serviceRequest, cancellationToken);

        return new GetControlTowerHealthResult(data.Data, data.TotalCount ?? data.Data.Count);
    }
}

public record GetControlTowerHealthQuery(
    int Page = 0,
    int PageSize = 100,
    int? DataSourceId = null,
    int? FolderId = null,
    HealthStatus? HealthStatus = null,
    bool? HasUnresolvedTasks = null,
    string? SearchKeyword = null) : IRequest<GetControlTowerHealthResult>;

public record GetControlTowerHealthResult(List<ControlTowerSubscriptionHealthData> Entries, int TotalCount);
