using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.DTOs;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Tasks;

internal sealed class GetTaskRelatedHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetTaskRelatedQuery, TaskRelatedResult>
{
    public async Task<TaskRelatedResult> Handle(GetTaskRelatedQuery request, CancellationToken cancellationToken)
    {
        var related = await Read(request.TaskId, cancellationToken);

        var items = related
            .Select(x =>
                new TaskRelatedItem(
                    x.Id,
                    x.CreatedAt,
                    x.LatestResultCount,
                    x.Resolved,
                    x.ResolvedAt))
            .ToList();

        return new TaskRelatedResult(request.TaskId, items);
    }

    internal async Task<List<RelatedTaskSummary>> Read(int taskId, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var queryId = await context.QueryTasks
            .Where(x => x.Id == taskId)
            .Select(x => x.Subscription.QueryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (queryId == 0)
        {
            return [];
        }

        // The other tasks of subscriptions on the same query, archived ones included; the last 20.
        return await context.QueryTasks
            .IgnoreQueryFilters()
            .Where(x => x.Subscription.QueryId == queryId)
            .Where(x => x.Id != taskId)
            .OrderByDescending(x => x.CreatedTime)
            .Take(20)
            .Select(x =>
                new RelatedTaskSummary(
                    x.Id,
                    x.CreatedTime,
                    x.LatestResultCount,
                    x.Resolved,
                    x.ResolvedAt))
            .ToListAsync(cancellationToken);
    }
}

public record GetTaskRelatedQuery(int TaskId) : IRequest<TaskRelatedResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Task(TaskId), ProjectAction.Read)
    ];
}

public record TaskRelatedResult(int TaskId, IReadOnlyList<TaskRelatedItem> Related);

public record TaskRelatedItem(int Id, DateTime CreatedAt, int LatestResultCount, bool Resolved, DateTime? ResolvedAt);
