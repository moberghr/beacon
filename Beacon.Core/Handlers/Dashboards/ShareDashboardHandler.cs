using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Handlers.Dashboards.ShareDashboard;

internal sealed class ShareDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext) : IRequestHandler<ShareDashboardCommand, Unit>
{
    public async Task<Unit> Handle(ShareDashboardCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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
