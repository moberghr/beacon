using System.Linq.Expressions;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class GetSubscriptionsHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetSubscriptionsQuery, PagedList<SubscriptionEntry>>
{
    public async Task<PagedList<SubscriptionEntry>> Handle(GetSubscriptionsQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The archived view drops the soft-delete filters for the whole query, so the recipient projections
        // re-apply theirs explicitly — an archived recipient must not reappear on either view.
        var subscriptions = request.Archived
            ? context.Subscriptions
                .IgnoreQueryFilters()
                .Where(x => x.ArchivedTime != null)
            : context.Subscriptions;

        // A subscription is readable as its query: through one readable project holding every step's source; the owner of
        // a query without steps reads its subscriptions. An archived one whose query has no steps left is read by the
        // sources its last run recorded, and with none it is an Admin's.
        var readableSubscriptions = request.Archived && readable.Ids != null
            ? subscriptions.Where(ArchivedReadable(context, readable))
            : subscriptions.WhereAllSourcesInOneProject(context, readable, x => x.Query.Steps.Select(y => y.DataSourceId), x => x.Query.OwnerUserId);

        return await readableSubscriptions
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Query.Name.Contains(request.Search!))
            .Select(x =>
                new SubscriptionEntry
                {
                    Id = x.Id,
                    QueryId = x.QueryId,
                    QueryName = x.Query.Name,
                    CronExpression = x.CronExpression,
                    RecipientCount = x.Recipients.Count(y => y.ArchivedTime == null),
                    RecipientNames = x.Recipients
                        .Where(y => y.ArchivedTime == null)
                        .Select(y => y.Name)
                        .ToList(),
                    AiActorId = x.AiActorId,
                    AiActorName = x.AiActor != null ? x.AiActor.Name : null,
                    CreateTasks = x.CreateTasks,
                    StoreResults = x.StoreResults,
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "queryName");
    }

    // An archived subscription is read by its query's live steps, or, when the query has none left, by the sources its
    // last run recorded; with neither it is an Admin's.
    internal static Expression<Func<Subscription, bool>> ArchivedReadable(BeaconContext context, ProjectSet readable)
    {
        if (readable.Ids == null)
        {
            return x => true;
        }

        var liveReachable = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<Subscription>(
            context,
            readable,
            x => x.Query.Steps.Select(y => y.DataSourceId));
        var lastRunReachable = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<QueryExecutionHistory>(
            context,
            readable,
            x => x.DataSourceIds!);
        Expression<Func<Subscription, bool>> lastRun = x => !x.Query.Steps.Any()
            && context.QueryExecutionHistory
                .Where(y => y.SubscriptionId == x.Id)
                .Where(y => y.DataSourceIds != null)
                .OrderByDescending(y => y.Id)
                .Take(1)
                .Any(lastRunReachable);

        return Expression.Lambda<Func<Subscription, bool>>(
            Expression.OrElse(
                liveReachable.Body,
                ReplacingExpressionVisitor.Replace(lastRun.Parameters[0], liveReachable.Parameters[0], lastRun.Body)),
            liveReachable.Parameters);
    }
}

/// <summary>
/// Alphabetical by query name unless <c>sort</c> says otherwise; <c>search</c> matches the query name.
/// <c>archived</c> switches the list from active to archived subscriptions.
/// </summary>
public record GetSubscriptionsQuery : ListRequest, IRequest<PagedList<SubscriptionEntry>>, IProjectFilteredRequest
{
    public string? Search { get; init; }

    public bool Archived { get; init; }
}

public record SubscriptionEntry
{
    public int Id { get; init; }

    public int QueryId { get; init; }

    public string QueryName { get; init; } = string.Empty;

    public string CronExpression { get; init; } = string.Empty;

    public int RecipientCount { get; init; }

    public List<string> RecipientNames { get; init; } = [];

    public int? AiActorId { get; init; }

    public string? AiActorName { get; init; }

    public bool CreateTasks { get; init; }

    public bool StoreResults { get; init; }
}
