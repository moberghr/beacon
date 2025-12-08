using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.QueryExecutionHistory;

namespace Semantico.Core.Services;

internal class StatisticsService(IDbContextFactory<SemanticoContext> contextFactory) : IStatisticsService
{
    public async Task<DashboardStatisticsData> GetDashboardStatistics(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var notifications = await context.QueryExecutionHistory
            .GroupBy(x => 1)
            .Select(x => new NotificationDateStatisticsData()
            {
                TotalQueries = x.Count(),
                NotificationsSent = x.Count(y => y.NotificationStatus == NotificationStatus.NotificationSent)
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new DashboardStatisticsData
        {
            TotalSubscriptions = await context.Subscriptions.IgnoreQueryFilters().CountAsync(cancellationToken),
            TotalQueries = await context.Queries.CountAsync(cancellationToken),
            TotalQueriesExecuted = notifications?.TotalQueries ?? 0,
            TotalNotificationsSent = notifications?.NotificationsSent ?? 0,
            ActiveSubscriptions = await context.Subscriptions.CountAsync(cancellationToken),
            TotalMigrationJobs = await context.MigrationJobs.CountAsync(cancellationToken),
            EnabledMigrationJobs = await context.MigrationJobs.CountAsync(m => m.IsEnabled, cancellationToken),
            TotalMigrationExecutions = await context.MigrationExecutions.CountAsync(cancellationToken),
            SuccessfulMigrationExecutions = await context.MigrationExecutions.CountAsync(m => m.Status == MigrationStatus.Completed, cancellationToken),
            TotalTasks = await context.QueryTasks.CountAsync(cancellationToken),
            UnresolvedTasks = await context.QueryTasks.CountAsync(t => !t.Resolved, cancellationToken),
            ResolvedTasks = await context.QueryTasks.CountAsync(t => t.Resolved, cancellationToken)
        };
    }
}

public interface IStatisticsService
{
    Task<DashboardStatisticsData> GetDashboardStatistics(CancellationToken cancellationToken);
}