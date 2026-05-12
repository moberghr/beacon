using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerSubscriptionDetailHandler(IControlTowerService controlTowerService)
    : IRequestHandler<GetControlTowerSubscriptionDetailQuery, GetControlTowerSubscriptionDetailResult>
{
    public async Task<GetControlTowerSubscriptionDetailResult> Handle(
        GetControlTowerSubscriptionDetailQuery request,
        CancellationToken cancellationToken)
    {
        var detail = await controlTowerService.GetSubscriptionDetail(
            request.SubscriptionId,
            request.TimeRangeDays,
            cancellationToken);

        if (detail == null)
        {
            throw new InvalidOperationException($"Subscription {request.SubscriptionId} not found.");
        }

        return new GetControlTowerSubscriptionDetailResult(detail);
    }
}

public record GetControlTowerSubscriptionDetailQuery(
    int SubscriptionId,
    int TimeRangeDays = 30) : IRequest<GetControlTowerSubscriptionDetailResult>;

public record GetControlTowerSubscriptionDetailResult(ControlTowerSubscriptionDetail Detail);
