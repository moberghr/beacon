using Beacon.Core.Models.ControlTower;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;
using Microsoft.AspNetCore.Http;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerSubscriptionDetailHandler(
    IControlTowerService controlTowerService,
    IHttpContextAccessor httpContextAccessor)
    : IRequestHandler<GetControlTowerSubscriptionDetailQuery, GetControlTowerSubscriptionDetailResult>
{
    public async Task<GetControlTowerSubscriptionDetailResult> Handle(
        GetControlTowerSubscriptionDetailQuery request,
        CancellationToken cancellationToken)
    {
        // The recent runs are the stored runs the caller may read (StoredRunAccess).
        var detail = await controlTowerService.GetSubscriptionDetail(
            request.SubscriptionId,
            request.TimeRangeDays,
            StoredRunAccess.ScopeOf(httpContextAccessor.HttpContext?.User),
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
