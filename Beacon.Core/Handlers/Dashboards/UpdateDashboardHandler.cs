using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.UpdateDashboard;

internal sealed class UpdateDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<UpdateDashboardCommand, Unit>
{
    public async Task<Unit> Handle(UpdateDashboardCommand request, CancellationToken cancellationToken)
    {
        // Check Edit permission on dashboard
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            request.DashboardId,
            userId,
            DashboardPermissionLevel.Edit,
            cancellationToken);

        if (!hasPermission)
        {
            throw new UnauthorizedAccessException("Insufficient permissions to update this dashboard");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboard = await context.Dashboards
            .FirstOrDefaultAsync(d => d.Id == request.DashboardId, cancellationToken);

        if (dashboard == null)
        {
            throw new InvalidOperationException($"Dashboard {request.DashboardId} not found");
        }

        dashboard.Name = request.Name;
        dashboard.Description = request.Description;
        dashboard.IsShared = request.IsShared;
        dashboard.IsDefault = request.IsDefault;
        dashboard.RefreshIntervalSeconds = request.RefreshIntervalSeconds;

        if (request.LayoutConfiguration != null)
        {
            dashboard.LayoutConfiguration = request.LayoutConfiguration;
        }

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record UpdateDashboardCommand(
    int DashboardId,
    string Name,
    string? Description,
    bool IsShared,
    bool IsDefault,
    int? RefreshIntervalSeconds,
    string? LayoutConfiguration
) : IRequest<Unit>;
