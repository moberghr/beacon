using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Notifications;

/// <summary>
/// Who may read a subscription run (<see cref="QueryExecutionHistory"/>): its metadata, its failure reason, and the
/// result rows stored when the subscription has <c>StoreResults</c> on. Every reader of stored runs (the notifications
/// endpoints, the Home activity feed, the Control Tower detail, a task's executions and result history, a
/// subscription's anomaly chart) applies <see cref="WhereReadableWithin"/> with the scope from <see cref="ScopeOfAsync"/>.
/// </summary>
internal static class StoredRunAccess
{
    /// <summary>The runs the current caller may read: where its project access allows <see cref="ProjectAction.Read"/>.</summary>
    public static async Task<StoredRunScope> ScopeOfAsync(IProjectAccessEvaluator evaluator, CancellationToken cancellationToken)
    {
        var scope = await evaluator.GetScopeAsync(cancellationToken);

        return StoredRunScope.From(scope.For(ProjectAction.Read));
    }

    /// <summary>
    /// Keeps the runs readable within <paramref name="scope"/>, by the data sources each run recorded and their current
    /// project links: a run is readable when one of the allowed projects holds every data source it read (the rule
    /// saved-query tools follow), or, when the scope includes the openly reachable sources, when every one of them is
    /// openly reachable. A run recorded before Beacon kept its data sources is read by its subscription's live query
    /// the same way, unless the caller is confined to a project list. A run with no data source to go by (an empty record,
    /// a query without steps, an archived subscription or query) is an Admin's.
    /// </summary>
    public static IQueryable<QueryExecutionHistory> WhereReadableWithin(
        this IQueryable<QueryExecutionHistory> runs,
        BeaconContext context,
        StoredRunScope scope)
    {
        if (scope.AllowedProjectIds == null)
        {
            return runs;
        }

        var allowed = scope.AllowedProjectIds.ToList();
        var notOpen = scope.NotOpenDataSourceIds.ToList();
        Expression<Func<QueryExecutionHistory, bool>> recorded = scope.IncludesOpen
            ? x => x.DataSourceIds != null
                && x.DataSourceIds.Length > 0
                && (allowed
                        .Any(y => x.DataSourceIds
                            .All(z => context.ProjectDataSources
                                .Where(w => w.ProjectId == y)
                                .Any(w => w.DataSourceId == z)))
                    || x.DataSourceIds.All(y => !notOpen.Contains(y)))
            : x => x.DataSourceIds != null
                && x.DataSourceIds.Length > 0
                && allowed
                    .Any(y => x.DataSourceIds
                        .All(z => context.ProjectDataSources
                            .Where(w => w.ProjectId == y)
                            .Any(w => w.DataSourceId == z)));
        if (!scope.IncludesLegacyRuns)
        {
            return runs.Where(recorded);
        }

        Expression<Func<QueryExecutionHistory, bool>> legacy = scope.IncludesOpen
            ? x => x.DataSourceIds == null
                && context.Subscriptions
                    .Where(y => y.Id == x.SubscriptionId)
                    .Where(y => y.ArchivedTime == null)
                    .Where(y => y.Query.ArchivedTime == null)
                    .Where(y => y.Query.Steps.Any())
                    .Any(y => allowed
                            .Any(z => y.Query.Steps
                                .All(w => context.ProjectDataSources
                                    .Where(v => v.ProjectId == z)
                                    .Any(v => v.DataSourceId == w.DataSourceId)))
                        || y.Query.Steps.All(z => !notOpen.Contains(z.DataSourceId)))
            : x => x.DataSourceIds == null
                && context.Subscriptions
                    .Where(y => y.Id == x.SubscriptionId)
                    .Where(y => y.ArchivedTime == null)
                    .Where(y => y.Query.ArchivedTime == null)
                    .Where(y => y.Query.Steps.Any())
                    .Any(y => allowed
                        .Any(z => y.Query.Steps
                            .All(w => context.ProjectDataSources
                                .Where(v => v.ProjectId == z)
                                .Any(v => v.DataSourceId == w.DataSourceId))));

        return runs.Where(Expression.Lambda<Func<QueryExecutionHistory, bool>>(
            Expression.OrElse(recorded.Body, ReplacingExpressionVisitor.Replace(legacy.Parameters[0], recorded.Parameters[0], legacy.Body)),
            recorded.Parameters));
    }

    /// <summary>Keeps the notifications whose run is readable within <paramref name="scope"/> (<see cref="WhereReadableWithin"/>).</summary>
    public static IQueryable<Notification> WhereRunReadableWithin(
        this IQueryable<Notification> notifications,
        BeaconContext context,
        StoredRunScope scope)
    {
        if (scope.AllowedProjectIds == null)
        {
            return notifications;
        }

        var runs = context.QueryExecutionHistory.WhereReadableWithin(context, scope);

        return notifications.Where(x => runs.Any(y => y.Id == x.QueryExecutionHistoryId));
    }
}
