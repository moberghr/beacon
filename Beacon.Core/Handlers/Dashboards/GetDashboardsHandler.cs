using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Dashboards;

namespace Beacon.Core.Handlers.Dashboards.GetDashboards;

internal sealed class GetDashboardsHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext) : IRequestHandler<GetDashboardsQuery, DashboardsListData>
{
    public async Task<DashboardsListData> Handle(GetDashboardsQuery query, CancellationToken cancellationToken)
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

        // Get total count
        var totalCount = await dashboardQuery.CountAsync(cancellationToken);

        // Apply pagination
        var dashboards = await dashboardQuery
            .OrderByDescending(d => d.IsDefault)
            .ThenBy(d => d.SortOrder)
            .ThenByDescending(d => d.CreatedTime)
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .Select(d => new DashboardListData
            {
                Id = d.Id,
                Name = d.Name,
                Description = d.Description,
                IsShared = d.IsShared,
                IsDefault = d.IsDefault,
                WidgetCount = d.Widgets.Count,
                CreatedTime = d.CreatedTime,
                IsOwner = d.CreatedByUserId == userId,
                CreatedByUserName = d.CreatedByUserName
            })
            .ToListAsync(cancellationToken);

        return new DashboardsListData
        {
            Data = dashboards,
            TotalCount = totalCount
        };
    }
}

public record GetDashboardsQuery(
    GetDashboardsRequest Request
) : IRequest<DashboardsListData>;

public class DashboardsListData : IPagedListResponse<DashboardListData>
{
    public List<DashboardListData> Data { get; set; } = new();
    public int? TotalCount { get; set; }
}
