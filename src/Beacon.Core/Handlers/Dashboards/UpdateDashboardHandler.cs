using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.Dashboards.UpdateDashboard;

internal sealed class UpdateDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IDashboardService dashboardService,
    IBeaconUserContext userContext) : IRequestHandler<UpdateDashboardCommand, Unit>
{
    public async Task<Unit> Handle(UpdateDashboardCommand request, CancellationToken cancellationToken)
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
            throw new BeaconForbiddenException("Insufficient permissions to update this dashboard");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboard = await context.Dashboards
            .Where(d => d.Id == request.DashboardId)
            .FirstOrDefaultAsync(cancellationToken);

        if (dashboard == null)
        {
            throw new InvalidOperationException($"Dashboard {request.DashboardId} not found");
        }

        // Sharing a dashboard with everyone, or making it its owner's default, is the dashboard Admin's (its owner or an
        // Admin grantee): an Edit grantee changes the content only.
        var changesSharing = request.IsShared != dashboard.IsShared || request.IsDefault != dashboard.IsDefault;
        if (changesSharing
            && !await dashboardService.UserHasPermissionAsync(request.DashboardId, userId, DashboardPermissionLevel.Admin, cancellationToken))
        {
            throw new BeaconForbiddenException("Only the dashboard's owner or an admin grantee can share it or make it the default.");
        }

        dashboard.Name = request.Name;
        dashboard.Description = request.Description;
        dashboard.IsShared = request.IsShared;
        dashboard.IsDefault = request.IsDefault;
        dashboard.RefreshIntervalSeconds = request.RefreshIntervalSeconds;

        if (request.LayoutConfiguration != null)
        {
            dashboard.LayoutConfiguration = request.LayoutConfiguration;
        }

        // Default is exclusive per user — clear it on this user's other dashboards in the same save
        if (request.IsDefault)
        {
            var otherDefaults = await context.Dashboards
                .Where(x => x.CreatedByUserId == dashboard.CreatedByUserId)
                .Where(x => x.Id != dashboard.Id)
                .Where(x => x.IsDefault)
                .ToListAsync(cancellationToken);

            foreach (var other in otherDefaults)
            {
                other.IsDefault = false;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

/// <summary>
/// Updates a dashboard: its content needs the dashboard's Edit level, changing <c>IsShared</c> or <c>IsDefault</c> its
/// Admin level (403 otherwise).
/// </summary>
[ProjectScopeExempt("dashboards are project-less and carry their own access list")]
public record UpdateDashboardCommand(
    int DashboardId,
    string Name,
    string? Description,
    bool IsShared,
    bool IsDefault,
    int? RefreshIntervalSeconds,
    string? LayoutConfiguration
) : IRequest<Unit>;
