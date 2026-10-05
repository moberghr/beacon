using Beacon.Core.Data;
using Beacon.Core.Helpers;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Subscriptions;

internal sealed class GetSubscriptionsHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetSubscriptionsQuery, PagedList<SubscriptionEntry>>
{
    public async Task<PagedList<SubscriptionEntry>> Handle(GetSubscriptionsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // The archived view drops the soft-delete filters for the whole query, so the recipient projections
        // re-apply theirs explicitly — an archived recipient must not reappear on either view.
        var subscriptions = request.Archived
            ? context.Subscriptions
                .IgnoreQueryFilters()
                .Where(x => x.ArchivedTime != null)
            : context.Subscriptions;

        return await subscriptions
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
}

/// <summary>
/// Alphabetical by query name unless <c>sort</c> says otherwise; <c>search</c> matches the query name.
/// <c>archived</c> switches the list from active to archived subscriptions.
/// </summary>
public record GetSubscriptionsQuery : ListRequest, IRequest<PagedList<SubscriptionEntry>>
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
