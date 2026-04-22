using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.Dashboards;

namespace Beacon.Core.Services;

internal class DashboardService(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext) : IDashboardService
{
    public async Task<DashboardDetailsData?> GetDashboardWithPermissionCheckAsync(
        int dashboardId,
        string userId,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboard = await context.Dashboards
            .Include(d => d.Widgets)
            .Include(d => d.Permissions)
            .FirstOrDefaultAsync(d => d.Id == dashboardId, cancellationToken);

        if (dashboard == null)
        {
            return null;
        }

        // Check permission
        var permissionLevel = GetUserPermissionLevel(dashboard, userId);
        if (permissionLevel == null)
        {
            return null; // User has no access
        }

        return new DashboardDetailsData
        {
            Id = dashboard.Id,
            Name = dashboard.Name,
            Description = dashboard.Description,
            CreatedByUserId = dashboard.CreatedByUserId,
            CreatedByUserName = dashboard.CreatedByUserName,
            IsShared = dashboard.IsShared,
            IsDefault = dashboard.IsDefault,
            RefreshIntervalSeconds = dashboard.RefreshIntervalSeconds,
            LayoutConfiguration = dashboard.LayoutConfiguration,
            CreatedTime = dashboard.CreatedTime,
            Widgets = dashboard.Widgets.OrderBy(w => w.SortOrder).Select(w => new DashboardWidgetData
            {
                Id = w.Id,
                Title = w.Title,
                WidgetType = w.WidgetType,
                ConfigurationJson = w.ConfigurationJson,
                PositionX = w.PositionX,
                PositionY = w.PositionY,
                Width = w.Width,
                Height = w.Height,
                SortOrder = w.SortOrder,
                RefreshIntervalSeconds = w.RefreshIntervalSeconds
            }).ToList(),
            UserPermissionLevel = permissionLevel
        };
    }

    public async Task<bool> UserHasPermissionAsync(
        int dashboardId,
        string userId,
        DashboardPermissionLevel requiredLevel,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboard = await context.Dashboards
            .Include(d => d.Permissions)
            .FirstOrDefaultAsync(d => d.Id == dashboardId, cancellationToken);

        if (dashboard == null)
        {
            return false;
        }

        var userLevel = GetUserPermissionLevel(dashboard, userId);
        return userLevel != null && userLevel.Value >= requiredLevel;
    }

    private static DashboardPermissionLevel? GetUserPermissionLevel(Dashboard dashboard, string userId)
    {
        // Owner has Admin level
        if (dashboard.CreatedByUserId == userId)
        {
            return DashboardPermissionLevel.Admin;
        }

        // Check explicit permissions
        var permission = dashboard.Permissions.FirstOrDefault(p => p.UserId == userId);
        if (permission != null)
        {
            return permission.PermissionLevel;
        }

        // Shared dashboards are viewable by all
        if (dashboard.IsShared)
        {
            return DashboardPermissionLevel.View;
        }

        return null; // No access
    }
}

public interface IDashboardService
{
    Task<DashboardDetailsData?> GetDashboardWithPermissionCheckAsync(
        int dashboardId,
        string userId,
        CancellationToken cancellationToken);

    Task<bool> UserHasPermissionAsync(
        int dashboardId,
        string userId,
        DashboardPermissionLevel requiredLevel,
        CancellationToken cancellationToken);
}
