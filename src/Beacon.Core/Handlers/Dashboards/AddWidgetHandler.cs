using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.AddWidget;

internal sealed class AddWidgetHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<AddWidgetCommand, AddWidgetResult>
{
    public async Task<AddWidgetResult> Handle(AddWidgetCommand request, CancellationToken cancellationToken)
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
            throw new UnauthorizedAccessException("Insufficient permissions to add widgets to this dashboard");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Get max sort order
        var maxSortOrder = await context.DashboardWidgets
            .Where(w => w.DashboardId == request.DashboardId)
            .MaxAsync(w => (int?)w.SortOrder, cancellationToken) ?? 0;

        var widget = new DashboardWidget
        {
            DashboardId = request.DashboardId,
            Title = request.Title,
            WidgetType = request.WidgetType,
            ConfigurationJson = request.ConfigurationJson,
            PositionX = request.PositionX ?? 0,
            PositionY = request.PositionY ?? 0,
            Width = request.Width ?? 6,
            Height = request.Height ?? 2,
            SortOrder = maxSortOrder + 1,
            RefreshIntervalSeconds = request.RefreshIntervalSeconds,
            CreatedTime = DateTime.UtcNow
        };

        context.DashboardWidgets.Add(widget);
        await context.SaveChangesAsync(cancellationToken);

        return new AddWidgetResult { WidgetId = widget.Id };
    }
}

public record AddWidgetCommand(
    int DashboardId,
    string Title,
    WidgetType WidgetType,
    string ConfigurationJson,
    int? PositionX,
    int? PositionY,
    int? Width,
    int? Height,
    int? RefreshIntervalSeconds
) : IRequest<AddWidgetResult>;

public record AddWidgetResult
{
    public int WidgetId { get; init; }
}
