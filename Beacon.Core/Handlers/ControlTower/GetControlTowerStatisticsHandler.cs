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
        var stats = await controlTowerService.GetControlTowerStatistics(cancellationToken);

        return new GetControlTowerStatisticsResult(stats);
    }
}

public record GetControlTowerStatisticsQuery : IRequest<GetControlTowerStatisticsResult>;

public record GetControlTowerStatisticsResult(ControlTowerStatistics Statistics);
