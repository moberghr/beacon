using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Dashboards;

namespace Beacon.Core.Handlers.Dashboards.GetDashboards;

internal sealed class GetDashboardsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext) : IRequestHandler<GetDashboardsQuery, PagedList<DashboardListData>>
{
    public async Task<PagedList<DashboardListData>> Handle(GetDashboardsQuery query, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var userId = userContext.UserId ?? string.Empty;
        var request = query.Request;

        var dashboardQuery = context.Dashboards
            .AsQueryable();

        // Filter by ownership or shared access
        dashboardQuery = dashboardQuery.Where(d =>
            d.CreatedByUserId == userId ||
            d.IsShared ||
            d.Permissions.Any(p => p.UserId == userId));

        // Apply filters
        if (request.IsShared.HasValue)
        {
            dashboardQuery = dashboardQuery.Where(d => d.IsShared == request.IsShared.Value);
        }

        if (request.IsDefault.HasValue)
        {
            dashboardQuery = dashboardQuery.Where(d => d.IsDefault == request.IsDefault.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SearchKeyword))
        {
            dashboardQuery = dashboardQuery.Where(d => d.Name.Contains(request.SearchKeyword) ||
                                     (d.Description != null && d.Description.Contains(request.SearchKeyword)));
        }

        return await dashboardQuery
            .Select(d => new DashboardListData
            {
                Id = d.Id,
                Name = d.Name,
                Description = d.Description,
                IsShared = d.IsShared,
                IsDefault = d.IsDefault,
                SortOrder = d.SortOrder,
                WidgetCount = d.Widgets.Count,
                CreatedTime = d.CreatedTime,
                IsOwner = d.CreatedByUserId == userId,
                CreatedByUserName = d.CreatedByUserName
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-isDefault,sortOrder,-createdTime");
    }
}

public record GetDashboardsQuery(
    GetDashboardsRequest Request
) : IRequest<PagedList<DashboardListData>>;
