using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.DeleteWidget;

internal sealed class DeleteWidgetHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<DeleteWidgetCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWidgetCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var widget = await context.DashboardWidgets
            .Include(w => w.Dashboard)
            .Where(w => w.Id == request.WidgetId)
            .FirstOrDefaultAsync(cancellationToken);

        if (widget == null)
        {
            return Unit.Value;
        }

        // Check Edit permission on parent dashboard
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            widget.DashboardId,
            userId,
            DashboardPermissionLevel.Edit,
            cancellationToken);

        if (!hasPermission)
        {
            throw new UnauthorizedAccessException("Insufficient permissions to delete widgets from this dashboard");
        }

        context.DashboardWidgets.Remove(widget);
        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record DeleteWidgetCommand(int WidgetId) : IRequest<Unit>;
