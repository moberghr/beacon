using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Dashboards.CreateDashboard;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.CloneDashboard;

internal sealed class CloneDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<CloneDashboardCommand, CreateDashboardResult>
{
    public async Task<CreateDashboardResult> Handle(CloneDashboardCommand request, CancellationToken cancellationToken)
    {
        // Caller must at least be able to view the source dashboard before cloning it
        var userId = userContext.UserId ?? string.Empty;
        var hasPermission = await dashboardService.UserHasPermissionAsync(
            request.SourceDashboardId,
            userId,
            DashboardPermissionLevel.View,
            cancellationToken);

        if (!hasPermission)
        {
            throw new InvalidOperationException("Insufficient permissions to clone this dashboard");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var sourceDashboard = await context.Dashboards
            .Include(d => d.Widgets)
            .Where(d => d.Id == request.SourceDashboardId)
            .FirstOrDefaultAsync(cancellationToken);

        if (sourceDashboard == null)
        {
            throw new InvalidOperationException($"Dashboard {request.SourceDashboardId} not found");
        }

        // Create cloned dashboard
        var clonedDashboard = new Dashboard
        {
            Name = request.NewName,
            Description = sourceDashboard.Description,
            CreatedByUserId = userContext.UserId,
            CreatedByUserName = userContext.DisplayName ?? userContext.UserName,
            IsShared = false, // Clones are private by default
            IsDefault = false,
            RefreshIntervalSeconds = sourceDashboard.RefreshIntervalSeconds,
            LayoutConfiguration = sourceDashboard.LayoutConfiguration,
            SortOrder = sourceDashboard.SortOrder,
            CreatedTime = DateTime.UtcNow
        };

        // Clone widgets onto the new dashboard's navigation so they persist in the same save
        foreach (var sourceWidget in sourceDashboard.Widgets)
        {
            var clonedWidget = new DashboardWidget
            {
                DashboardId = clonedDashboard.Id,
                Title = sourceWidget.Title,
                WidgetType = sourceWidget.WidgetType,
                ConfigurationJson = sourceWidget.ConfigurationJson,
                PositionX = sourceWidget.PositionX,
                PositionY = sourceWidget.PositionY,
                Width = sourceWidget.Width,
                Height = sourceWidget.Height,
                SortOrder = sourceWidget.SortOrder,
                RefreshIntervalSeconds = sourceWidget.RefreshIntervalSeconds,
                CreatedTime = DateTime.UtcNow
            };

            clonedDashboard.Widgets.Add(clonedWidget);
        }

        context.Dashboards.Add(clonedDashboard);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateDashboardResult { DashboardId = clonedDashboard.Id };
    }
}

public record CloneDashboardCommand(
    int SourceDashboardId,
    string NewName
) : IRequest<CreateDashboardResult>;
