using MediatR;
using Semantico.Core.Authorization;
using Semantico.Core.Models.Dashboards;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Dashboards.GetDashboard;

internal sealed class GetDashboardHandler(
    IDashboardService dashboardService,
    ISemanticoUserContext userContext) : IRequestHandler<GetDashboardQuery, DashboardDetailsData?>
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
