using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.DeleteDashboard;

internal sealed class DeleteDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<DeleteDashboardCommand, Unit>
{
    public async Task<Unit> Handle(DeleteDashboardCommand request, CancellationToken cancellationToken)
    {
        // Check Admin permission on dashboard (only owner/admin can delete)
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            request.DashboardId,
            userId,
            DashboardPermissionLevel.Admin,
            cancellationToken);

        if (!hasPermission)
        {
            throw new UnauthorizedAccessException("Only dashboard owner or admin can delete this dashboard");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboard = await context.Dashboards
            .Where(d => d.Id == request.DashboardId)
            .FirstOrDefaultAsync(cancellationToken);

        if (dashboard != null)
        {
            // Soft delete (set ArchivedTime)
            dashboard.ArchivedTime = DateTime.UtcNow;
            await context.SaveChangesAsync(cancellationToken);
        }

        return Unit.Value;
    }
}

public record DeleteDashboardCommand(int DashboardId) : IRequest<Unit>;
