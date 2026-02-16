using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Authorization;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;
using Semantico.Core.Handlers.Dashboards.CreateDashboard;

namespace Semantico.Core.Handlers.Dashboards.CloneDashboard;

internal sealed class CloneDashboardHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    ISemanticoUserContext userContext) : IRequestHandler<CloneDashboardCommand, CreateDashboardResult>
{
    public async Task<CreateDashboardResult> Handle(CloneDashboardCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var sourceDashboard = await context.Dashboards
            .Include(d => d.Widgets)
            .FirstOrDefaultAsync(d => d.Id == request.SourceDashboardId, cancellationToken);

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

        context.Dashboards.Add(clonedDashboard);
        await context.SaveChangesAsync(cancellationToken);

        // Clone widgets
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

            context.DashboardWidgets.Add(clonedWidget);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new CreateDashboardResult { DashboardId = clonedDashboard.Id };
    }
}

public record CloneDashboardCommand(
    int SourceDashboardId,
    string NewName
) : IRequest<CreateDashboardResult>;
