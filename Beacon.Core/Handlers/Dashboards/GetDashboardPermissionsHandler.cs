using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Handlers.Dashboards.GetDashboardPermissions;

internal sealed class GetDashboardPermissionsHandler(
    IDbContextFactory<BeaconContext> contextFactory) : IRequestHandler<GetDashboardPermissionsQuery, List<DashboardPermissionData>>
{
    public async Task<List<DashboardPermissionData>> Handle(GetDashboardPermissionsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var permissions = await context.DashboardPermissions
            .Where(p => p.DashboardId == request.DashboardId)
            .Select(p => new DashboardPermissionData
            {
                Id = p.Id,
                UserId = p.UserId,
                UserName = context.Users
                    .Where(u => u.ExternalId == p.UserId)
                    .Select(u => u.DisplayName ?? u.UserName)
                    .FirstOrDefault() ?? p.UserId,
                PermissionLevel = p.PermissionLevel,
                GrantedAt = p.GrantedAt
            })
            .ToListAsync(cancellationToken);

        return permissions;
    }
}

public record GetDashboardPermissionsQuery(int DashboardId) : IRequest<List<DashboardPermissionData>>;

public record DashboardPermissionData
{
    public int Id { get; init; }
    public string UserId { get; init; } = null!;
    public string UserName { get; init; } = null!;
    public DashboardPermissionLevel PermissionLevel { get; init; }
    public DateTime GrantedAt { get; init; }
}
