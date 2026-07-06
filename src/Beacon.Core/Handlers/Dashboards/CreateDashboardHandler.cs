using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Handlers.Dashboards.CreateDashboard;

internal sealed class CreateDashboardHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconUserContext userContext) : IRequestHandler<CreateDashboardCommand, CreateDashboardResult>
{
    public async Task<CreateDashboardResult> Handle(CreateDashboardCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var dashboard = new Dashboard
        {
            Name = request.Name,
            Description = request.Description,
            CreatedByUserId = userContext.UserId,
            CreatedByUserName = userContext.DisplayName ?? userContext.UserName,
            IsShared = request.IsShared,
            RefreshIntervalSeconds = request.RefreshIntervalSeconds,
            CreatedTime = DateTime.UtcNow
        };

        context.Dashboards.Add(dashboard);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateDashboardResult { DashboardId = dashboard.Id };
    }
}

public record CreateDashboardCommand(
    string Name,
    string? Description,
    bool IsShared,
    int? RefreshIntervalSeconds
) : IRequest<CreateDashboardResult>;

public record CreateDashboardResult
{
    public int DashboardId { get; init; }
}
