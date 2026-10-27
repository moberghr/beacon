using Beacon.Core.Authorization;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.ControlTower;

internal sealed class GetControlTowerSubscriptionDetailHandler(
    ControlTowerService controlTowerService,
    IProjectAccessEvaluator projectAccess)
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
            await StoredRunAccess.ScopeOfAsync(projectAccess, cancellationToken),
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
    int TimeRangeDays = 30) : IRequest<GetControlTowerSubscriptionDetailResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Subscription(SubscriptionId), ProjectAction.Read)
    ];
}

public record GetControlTowerSubscriptionDetailResult(ControlTowerSubscriptionDetail Detail);
