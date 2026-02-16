using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Authorization;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Dashboards.DeleteDashboard;

internal sealed class DeleteDashboardHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDashboardService dashboardService,
    ISemanticoUserContext userContext) : IRequestHandler<DeleteDashboardCommand, Unit>
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
            .FirstOrDefaultAsync(d => d.Id == request.DashboardId, cancellationToken);

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
