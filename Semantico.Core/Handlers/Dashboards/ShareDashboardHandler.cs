using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Authorization;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.Dashboards.ShareDashboard;

internal sealed class ShareDashboardHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    ISemanticoUserContext userContext) : IRequestHandler<ShareDashboardCommand, Unit>
{
    public async Task<Unit> Handle(ShareDashboardCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Check if permission already exists
        var existingPermission = await context.DashboardPermissions
            .FirstOrDefaultAsync(p => p.DashboardId == request.DashboardId && p.UserId == request.UserId, cancellationToken);

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
