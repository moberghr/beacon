using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class StatisticsService : IStatisticsService
{
    private readonly SemanticoContext _context;

    public StatisticsService(SemanticoContext context)
    {
        _context = context;
    }

    public async Task<DashboardStatisticsData> GetDashboardStatistics(CancellationToken cancellationToken)
    {
        var notifications = await _context.QueryExecutionHistory
            .GroupBy(x => 1)
            .Select(x => new NotificationDateStatisticsData()
            {
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DashboardStatisticsData
        {
            TotalSubscriptions = await _context.Subscriptions.IgnoreQueryFilters().CountAsync(cancellationToken),
            TotalQueries = await _context.Queries.CountAsync(cancellationToken),
            TotalQueriesExecuted = notifications?.TotalQueries ?? 0,
            TotalNotificationsSent = notifications?.NotificationsSent ?? 0,
            ActiveSubscriptions = await _context.Subscriptions.CountAsync(cancellationToken)
        };
    }
}

public interface IStatisticsService
{
    Task<DashboardStatisticsData> GetDashboardStatistics(CancellationToken cancellationToken);
}