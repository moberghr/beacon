using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.ShareDashboard;

internal sealed class ShareDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<ShareDashboardCommand, Unit>
{
    public async Task<Unit> Handle(ShareDashboardCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboardExists = await context.Dashboards
            .Where(x => x.Id == request.DashboardId)
            .AnyAsync(cancellationToken);

        if (!dashboardExists)
        {
            throw new InvalidOperationException($"Dashboard {request.DashboardId} not found");
        }

        // Only an owner/admin of the dashboard may grant or change permissions
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            request.DashboardId,
            userId,
            DashboardPermissionLevel.Admin,
            cancellationToken);

        if (!hasPermission)
        {
            throw new InvalidOperationException("Only dashboard owner or admin can manage permissions");
        }

        // Check if permission already exists
        var existingPermission = await context.DashboardPermissions
            .Where(p => p.DashboardId == request.DashboardId && p.UserId == request.UserId)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingPermission != null)
        {
            // Update existing permission
            existingPermission.PermissionLevel = request.PermissionLevel;
        }
        else
        {
            // Create new permission
            var permission = new DashboardPermission
            {
                DashboardId = request.DashboardId,
                UserId = request.UserId,
                PermissionLevel = request.PermissionLevel,
                GrantedByUserId = userContext.UserId,
                GrantedAt = DateTime.UtcNow,
                CreatedTime = DateTime.UtcNow
            };

            context.DashboardPermissions.Add(permission);
        }

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record ShareDashboardCommand(
    int DashboardId,
    string UserId,
    DashboardPermissionLevel PermissionLevel
) : IRequest<Unit>;
