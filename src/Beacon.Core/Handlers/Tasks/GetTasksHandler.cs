using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.DTOs;
using Beacon.Core.Helpers;
using Beacon.Core.Models.Tasks;
using MediatR;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTasksHandler(IDbContextFactory<BeaconContext> contextFactory, IProjectAccessEvaluator projectAccess)
    : IRequestHandler<GetTasksQuery, PagedList<TaskEntry>>
{
    public async Task<PagedList<TaskEntry>> Handle(GetTasksQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var serviceRequest = new GetTasksRequest
        {
            Readable = scope.For(ProjectAction.Read),
            Page = request.Page,
            PageSize = request.PageSize,
            Sort = request.Sort,
            SubscriptionId = request.SubscriptionId,
            Resolved = request.Resolved,
        };

        var result = await Read(serviceRequest, cancellationToken);

        return result.Map(x =>
            new TaskEntry(
                x.Id,
                x.SubscriptionName,
                x.QueryName,
                x.LatestResultCount,
                x.NotificationCount,
                x.ExecutionCount,
                x.UniqueResultCounts,
                x.CreatedAt,
                x.Resolved,
                x.ResolvedAt,
                x.ResolvedByUserName,
                x.AiActorId,
                x.AiActorName));
    }

    internal async Task<PagedList<TaskData>> Read(GetTasksRequest request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.QueryTasks
            .WhereAllSourcesInOneProject(context, request.Readable, x => x.Subscription.Query.Steps.Select(y => y.DataSourceId), x => x.Subscription.Query.OwnerUserId);

        if (request.SubscriptionId.HasValue)
        {
            query = query.Where(x => x.SubscriptionId == request.SubscriptionId.Value);
        }

        if (request.Resolved.HasValue)
        {
            query = query.Where(x => x.Resolved == request.Resolved.Value);
        }

        return await query
            .Select(x =>
                new TaskData
                {
                    Id = x.Id,
                    SubscriptionName = x.Subscription.Query.Name,
                    QueryName = x.Subscription.Query.Name,
                    LatestResultCount = x.LatestResultCount,
                    LastNotificationAt = x.LastNotificationAt,
                    NotificationCount = x.Notifications.Count,
                    CreatedAt = x.CreatedTime,
                    Resolved = x.Resolved,
                    ResolvedAt = x.ResolvedAt,
                    // Named by display name or "User {id}", never by user name.
                    ResolvedByUserName = x.ResolvedByUserId == null
                        ? null
                        : context.Users
                            .Where(y => y.ExternalId == x.ResolvedByUserId)
                            .Select(UserLabel.OfUser)
                            .FirstOrDefault(),
                    AiActorId = x.Subscription.AiActorId,
                    AiActorName = x.Subscription.AiActor != null ? x.Subscription.AiActor.Name : null,
                    ExecutionCount = context.QueryExecutionHistory
                        .Where(y => y.SubscriptionId == x.SubscriptionId)
                        .Where(y => y.CreatedTime >= x.CreatedTime)
                        .Count(),
                    // Distinct result counts since task creation show the volatility.
                    UniqueResultCounts = context.QueryExecutionHistory
                        .Where(y => y.SubscriptionId == x.SubscriptionId)
                        .Where(y => y.CreatedTime >= x.CreatedTime)
                        .Select(y => y.ResultCount)
                        .Distinct()
                        .Count()
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdAt");
    }
}

public record GetTasksQuery : ListRequest, IRequest<PagedList<TaskEntry>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? SubscriptionId { get; init; }

    public bool? Resolved { get; init; }
}

public record TaskEntry(
    int Id,
    string SubscriptionName,
    string QueryName,
    int LatestResultCount,
    int NotificationCount,
    int ExecutionCount,
    int UniqueResultCounts,
    DateTime CreatedAt,
    bool Resolved,
    DateTime? ResolvedAt,
    string? ResolvedByUserName,
    int? AiActorId,
    string? AiActorName);
