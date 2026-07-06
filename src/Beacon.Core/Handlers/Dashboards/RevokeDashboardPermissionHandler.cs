using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.RevokeDashboardPermission;

internal sealed class RevokeDashboardPermissionHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<RevokeDashboardPermissionCommand, Unit>
{
    public async Task<Unit> Handle(RevokeDashboardPermissionCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var permission = await context.DashboardPermissions
            .Where(x => x.Id == request.PermissionId)
            .FirstOrDefaultAsync(cancellationToken);

        if (permission == null)
        {
            return Unit.Value;
        }

        // Only an owner/admin of the parent dashboard may revoke permissions
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            permission.DashboardId,
            userId,
            DashboardPermissionLevel.Admin,
            cancellationToken);

        if (!hasPermission)
        {
            throw new InvalidOperationException("Only dashboard owner or admin can revoke permissions");
        }

        context.DashboardPermissions.Remove(permission);
        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record RevokeDashboardPermissionCommand(int PermissionId) : IRequest<Unit>;
