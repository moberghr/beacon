using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Authorization;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.Dashboards.DeleteWidget;

internal sealed class DeleteWidgetHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    IDashboardService dashboardService,
    ISemanticoUserContext userContext) : IRequestHandler<DeleteWidgetCommand, Unit>
{
    public async Task<Unit> Handle(DeleteWidgetCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var widget = await context.DashboardWidgets
            .Include(w => w.Dashboard)
            .FirstOrDefaultAsync(w => w.Id == request.WidgetId, cancellationToken);

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
