using MediatR;
using Beacon.Core.Authorization;
using Beacon.Core.Models.Dashboards;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.GetDashboard;

internal sealed class GetDashboardHandler(
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<GetDashboardQuery, DashboardDetailsData?>
{
    public async Task<DashboardDetailsData?> Handle(GetDashboardQuery request, CancellationToken cancellationToken)
    {
        var userId = userContext.UserId ?? string.Empty;
        return await dashboardService.GetDashboardWithPermissionCheckAsync(
            request.DashboardId,
            userId,
            cancellationToken);
    }
}

public record GetDashboardQuery(int DashboardId) : IRequest<DashboardDetailsData?>;
